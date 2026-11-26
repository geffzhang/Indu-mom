using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Labeling;

public sealed class LabelJobProcessor : ILabelJobProcessor
{
    private static readonly int[] RetryDelaysSeconds = [5, 20, 60];
    private static readonly TimeSpan MarkerSimulationDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MarkerRegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Encoding GbkEncoding = CreateGbkEncoding();

    private readonly HoneyMomDbContext _context;
    private readonly DynamicEntityStore _store;
    private readonly IPrintDeviceTransport _transport;
    private readonly TimeProvider _timeProvider;

    public LabelJobProcessor(
        HoneyMomDbContext context,
        DynamicEntityStore store,
        IPrintDeviceTransport transport,
        TimeProvider timeProvider)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<IReadOnlyDictionary<string, object?>?> ProcessJobAsync(
        long jobId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(jobId);
        var databaseJobId = checked((int)jobId);
        var claim = await ClaimJobAsync(jobId, databaseJobId, cancellationToken);
        if (claim is null)
        {
            return null;
        }

        var attemptedAt = _timeProvider.GetLocalNow().DateTime;
        SendOutcome outcome;
        try
        {
            if (claim.Device is null)
            {
                outcome = new SendOutcome(false, "InvalidOperationException: 设备不存在");
            }
            else if (string.Equals(claim.Kind, "mark", StringComparison.Ordinal))
            {
                outcome = await SendMarkerAsync(
                    claim.Job,
                    claim.Device,
                    cancellationToken);
            }
            else
            {
                outcome = await SendPrinterAsync(
                    claim.Job,
                    claim.Device,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsDeviceFailure(exception))
        {
            outcome = new SendOutcome(
                false,
                $"{exception.GetType().Name}: {exception.Message}");
        }

        return await PersistOutcomeAsync(claim, outcome, attemptedAt, cancellationToken);
    }

    private async Task<ClaimedJob?> ClaimJobAsync(
        long jobId,
        int databaseJobId,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);
        var now = _timeProvider.GetLocalNow().DateTime;
        var affected = await _context.Set<Dictionary<string, object?>>("print_job")
            .Where(row =>
                EF.Property<int>(row, "id") == databaseJobId &&
                EF.Property<string>(row, "kind") != "browser" &&
                EF.Property<string>(row, "status") == "queued" &&
                (EF.Property<DateTime?>(row, "next_try") == null ||
                 EF.Property<DateTime?>(row, "next_try") <= now))
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(row => EF.Property<string>(row, "status"), "sending")
                    .SetProperty(row => EF.Property<DateTime?>(row, "updated_at"), now),
                cancellationToken);
        if (affected == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var job = await _store.GetAsync("print_job", jobId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Claimed print job {jobId} could not be read.");
        var kind = ReadString(job, "kind");
        var deviceEntity = string.Equals(kind, "mark", StringComparison.Ordinal)
            ? "marker"
            : "printer";
        var deviceId = ReadNullableInt64(job, deviceEntity);
        var device = deviceId is null
            ? null
            : await _store.GetAsync(deviceEntity, deviceId.Value, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ClaimedJob(jobId, kind, deviceEntity, deviceId, job, device);
    }

    private async Task<SendOutcome> SendPrinterAsync(
        IReadOnlyDictionary<string, object?> job,
        IReadOnlyDictionary<string, object?> printer,
        CancellationToken cancellationToken)
    {
        var payloadText = ReadString(job, "payload");
        var printerKind = ReadString(printer, "kind");
        var payload = string.Equals(printerKind, "tspl", StringComparison.Ordinal)
            ? GbkEncoding.GetBytes(payloadText)
            : Encoding.UTF8.GetBytes(payloadText);
        var host = ReadString(printer, "host");
        var port = GetPort(printer, "port", 9100);

        await _transport.SendPrinterAsync(
            host,
            port,
            payload,
            TimeSpan.FromSeconds(5),
            cancellationToken);

        return new SendOutcome(
            true,
            $"已发送 {payload.Length} 字节到 {host}:{port}");
    }

    private async Task<SendOutcome> SendMarkerAsync(
        IReadOnlyDictionary<string, object?> job,
        IReadOnlyDictionary<string, object?> marker,
        CancellationToken cancellationToken)
    {
        var command = Encoding.UTF8.GetBytes(ReadString(job, "payload"));
        var mode = ReadString(marker, "mode");
        var host = ReadString(marker, "host");
        var port = GetPort(marker, "port", 9000);
        var timeout = GetMarkerTimeout(marker);
        var response = string.Equals(mode, "sim", StringComparison.Ordinal) ||
            string.IsNullOrEmpty(host)
                ? await SimulateMarkerAsync(command, timeout, cancellationToken)
                : await _transport.SendMarkerAsync(
                    host,
                    port,
                    command,
                    timeout,
                    cancellationToken);

        if (string.IsNullOrEmpty(response))
        {
            return new SendOutcome(false, "打码机没有应答（超时）");
        }

        var errorPattern = ReadString(marker, "err");
        if (errorPattern.Length > 0 && Regex.IsMatch(
            response,
            errorPattern,
            RegexOptions.None,
            MarkerRegexTimeout))
        {
            return new SendOutcome(false, response);
        }

        var acknowledgmentPattern = ReadString(marker, "ack");
        if (acknowledgmentPattern.Length == 0)
        {
            acknowledgmentPattern = "OK";
        }

        if (Regex.IsMatch(
            response,
            acknowledgmentPattern,
            RegexOptions.None,
            MarkerRegexTimeout))
        {
            return new SendOutcome(true, response);
        }

        return new SendOutcome(false, $"应答不认识：{response}");
    }

    private async Task<string> SimulateMarkerAsync(
        ReadOnlyMemory<byte> command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var commandText = Encoding.UTF8.GetString(command.Span);
        var line = commandText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .FirstOrDefault();
        if (string.IsNullOrEmpty(line))
        {
            await Task.Delay(timeout, _timeProvider, cancellationToken);
            return string.Empty;
        }

        var parts = line.Split(' ', 3);
        var operation = parts[0].ToUpperInvariant();
        if (operation == "STATUS")
        {
            return "OK READY";
        }

        if (operation != "MARK" || parts.Length < 2)
        {
            return "ERR UNKNOWN";
        }

        if (timeout != Timeout.InfiniteTimeSpan && timeout <= MarkerSimulationDelay)
        {
            if (timeout > TimeSpan.Zero)
            {
                await Task.Delay(timeout, _timeProvider, cancellationToken);
            }

            return string.Empty;
        }

        await Task.Delay(MarkerSimulationDelay, _timeProvider, cancellationToken);
        var ok = parts.Length < 3 ||
            !parts[2].Contains("FAIL", StringComparison.Ordinal);
        return ok
            ? $"OK {parts[1]}"
            : $"ERR {parts[1]} 打标失败";
    }

    private async Task<IReadOnlyDictionary<string, object?>?> PersistOutcomeAsync(
        ClaimedJob claim,
        SendOutcome outcome,
        DateTime attemptedAt,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await _context.Database.BeginTransactionAsync(cancellationToken);
        var job = await _store.GetAsync("print_job", claim.JobId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Print job {claim.JobId} disappeared before its result was saved.");
        var attempts = checked((ReadInt32(job, "attempts") ?? 0) + 1);
        var message = Truncate(outcome.Message, 300);
        var log = ReadLogEntries(job.TryGetValue("log", out var currentLog) ? currentLog : null);
        log.Add(JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["t"] = FormatTimestamp(attemptedAt),
            ["ok"] = outcome.Succeeded,
            ["msg"] = message
        }));

        DateTime? nextTry = !outcome.Succeeded && attempts <= RetryDelaysSeconds.Length
            ? attemptedAt.AddSeconds(RetryDelaysSeconds[attempts - 1])
            : null;
        var status = outcome.Succeeded
            ? "done"
            : attempts <= RetryDelaysSeconds.Length
                ? "queued"
                : "failed";
        var affected = await _context.Set<Dictionary<string, object?>>("print_job")
            .Where(row =>
                EF.Property<int>(row, "id") == checked((int)claim.JobId) &&
                EF.Property<string>(row, "status") == "sending")
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(row => EF.Property<string>(row, "status"), status)
                    .SetProperty(row => EF.Property<int?>(row, "attempts"), attempts)
                    .SetProperty(
                        row => EF.Property<string?>(row, "last_error"),
                        outcome.Succeeded ? null : Truncate(outcome.Message, 190))
                    .SetProperty(row => EF.Property<string?>(row, "log"), JsonSerializer.Serialize(log))
                    .SetProperty(row => EF.Property<DateTime?>(row, "next_try"), nextTry)
                    .SetProperty(
                        row => EF.Property<DateTime?>(row, "sent_at"),
                        outcome.Succeeded
                            ? attemptedAt
                            : ReadNullableDateTime(job, "sent_at"))
                    .SetProperty(row => EF.Property<DateTime?>(row, "updated_at"), attemptedAt),
                cancellationToken);
        if (affected == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return await _store.GetAsync("print_job", claim.JobId, cancellationToken);
        }

        if (claim.DeviceId is long deviceId && claim.Device is not null)
        {
            if (outcome.Succeeded)
            {
                await _context.Set<Dictionary<string, object?>>(claim.DeviceEntity)
                    .Where(row => EF.Property<int>(row, "id") == checked((int)deviceId))
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(row => EF.Property<string>(row, "status"), "ok")
                            .SetProperty(row => EF.Property<DateTime?>(row, "last_ok"), attemptedAt)
                            .SetProperty(row => EF.Property<DateTime?>(row, "updated_at"), attemptedAt),
                        cancellationToken);
            }
            else
            {
                await _context.Set<Dictionary<string, object?>>(claim.DeviceEntity)
                    .Where(row => EF.Property<int>(row, "id") == checked((int)deviceId))
                    .ExecuteUpdateAsync(
                        updates => updates
                            .SetProperty(row => EF.Property<string>(row, "status"), "error")
                            .SetProperty(
                                row => EF.Property<string?>(row, "last_error"),
                                Truncate(outcome.Message, 190))
                            .SetProperty(row => EF.Property<DateTime?>(row, "updated_at"), attemptedAt),
                        cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await _store.GetAsync("print_job", claim.JobId, cancellationToken);
    }

    private static List<JsonElement> ReadLogEntries(object? value)
    {
        if (value is null)
        {
            return [];
        }

        var json = value switch
        {
            string text => text,
            JsonDocument document => document.RootElement.GetRawText(),
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(value)
        };
        using var parsed = JsonDocument.Parse(json);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("A print-job log must be a JSON array.");
        }

        return parsed.RootElement.EnumerateArray()
            .Select(entry => entry.Clone())
            .ToList();
    }

    private static string ReadString(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return string.Empty;
        }

        return value is string text
            ? text
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static int? ReadInt32(
        IReadOnlyDictionary<string, object?> values,
        string key) =>
        values.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : null;

    private static long? ReadNullableInt64(
        IReadOnlyDictionary<string, object?> values,
        string key) =>
        values.TryGetValue(key, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;

    private static DateTime? ReadNullableDateTime(
        IReadOnlyDictionary<string, object?> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset.DateTime,
            _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture)
        };
    }

    private static int GetPort(
        IReadOnlyDictionary<string, object?> values,
        string key,
        int defaultPort)
    {
        var port = ReadInt32(values, key);
        return port is null or 0 ? defaultPort : port.Value;
    }

    private static TimeSpan GetMarkerTimeout(IReadOnlyDictionary<string, object?> marker)
    {
        var milliseconds = ReadInt32(marker, "timeout_ms");
        return TimeSpan.FromMilliseconds(milliseconds is null or 0 ? 5000 : milliseconds.Value);
    }

    private static List<JsonElement> ReadLogEntries(IReadOnlyDictionary<string, object?> job) =>
        ReadLogEntries(job.TryGetValue("log", out var value) ? value : null);

    private static string FormatTimestamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool IsDeviceFailure(Exception exception) =>
        exception is SocketException or
            IOException or
            TimeoutException or
            ArgumentException or
            ObjectDisposedException or
            FormatException or
            InvalidCastException or
            OverflowException;

    private static string Truncate(string value, int maxLength)
    {
        var result = new StringBuilder(Math.Min(value.Length, maxLength));
        var count = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (count++ == maxLength)
            {
                break;
            }

            result.Append(rune);
        }

        return count <= maxLength ? value : result.ToString();
    }

    private static Encoding CreateGbkEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            936,
            EncoderFallback.ReplacementFallback,
            DecoderFallback.ReplacementFallback);
    }

    private sealed record ClaimedJob(
        long JobId,
        string Kind,
        string DeviceEntity,
        long? DeviceId,
        IReadOnlyDictionary<string, object?> Job,
        IReadOnlyDictionary<string, object?>? Device);

    private sealed record SendOutcome(bool Succeeded, string Message);
}
