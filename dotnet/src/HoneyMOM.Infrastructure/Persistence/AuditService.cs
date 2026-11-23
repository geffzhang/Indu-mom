using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class AuditService
{
    private static readonly SemaphoreSlim SqliteWriteLock = new(1, 1);
    private static readonly HashSet<string> SensitiveFields = new(StringComparer.Ordinal)
    {
        "password_hash",
        "password",
        "key_hash",
        "token",
        "api_key",
        "secret"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task AppendAsync(
        HoneyMomDbContext context,
        AuditEntry entry,
        CancellationToken cancellationToken)
    {
        var acquiredSqliteLock = context.Database.IsSqlite();
        if (acquiredSqliteLock)
        {
            await SqliteWriteLock.WaitAsync(cancellationToken);
        }

        IDbContextTransaction? transaction = null;
        var ownsTransaction = context.Database.CurrentTransaction is null;
        try
        {
            if (ownsTransaction)
            {
                transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            }

            var diff = Redact(entry.Diff);
            var time = entry.Time ?? DateTime.Now;
            var user = entry.User ?? "system";
            var kind = entry.Kind ?? entry.Action;
            var target = entry.Target ?? DefaultTarget(entry) ?? "";
            var request = AuditRequestContext.Current;
            var source = request?.Source ?? entry.Source ?? "ui";
            context.AuditLogs.Add(new AuditLogRow
            {
                Time = time.AddTicks(-(time.Ticks % TimeSpan.TicksPerSecond)),
                User = user[..Math.Min(user.Length, 80)],
                Kind = kind[..Math.Min(kind.Length, 40)],
                Target = target[..Math.Min(target.Length, 190)],
                Entity = entry.Entity,
                EntityId = entry.EntityId,
                Action = entry.Action[..Math.Min(entry.Action.Length, 60)],
                Diff = diff is null ? null : SerializeJson(diff),
                Detail = entry.Detail is null ? null : SerializeJson(entry.Detail),
                Source = source[..Math.Min(source.Length, 20)],
                Ip = entry.Ip ?? request?.Ip
            });
            await context.SaveChangesAsync(cancellationToken);
            if (ownsTransaction)
            {
                await transaction!.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
            if (acquiredSqliteLock)
            {
                SqliteWriteLock.Release();
            }
        }
    }

    public async Task SealAsync(
        HoneyMomDbContext context,
        CancellationToken cancellationToken)
    {
        var acquiredSqliteLock = context.Database.IsSqlite();
        if (acquiredSqliteLock)
        {
            await SqliteWriteLock.WaitAsync(cancellationToken);
        }

        IDbContextTransaction? transaction = null;
        var ownsTransaction = context.Database.CurrentTransaction is null;
        try
        {
            if (ownsTransaction)
            {
                transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            }

            if (context.Database.IsNpgsql())
            {
                await context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtext('honeymes.audit.seal'))",
                    cancellationToken);
            }
            else if (context.Database.IsSqlite())
            {
                await context.Database.ExecuteSqlRawAsync(
                    "UPDATE audit_log SET seq = seq WHERE 1 = 0",
                    cancellationToken);
            }

            var last = await context.AuditLogs
                .AsNoTracking()
                .Where(row => row.Seq != null)
                .OrderByDescending(row => row.Seq)
                .Select(row => new { row.Seq, row.Hash })
                .FirstOrDefaultAsync(cancellationToken);
            var sequence = last?.Seq ?? 0;
            var previousHash = last?.Hash ?? "";

            while (true)
            {
                var rows = await context.AuditLogs
                    .Where(row => row.Seq == null)
                    .OrderBy(row => row.Id)
                    .Take(5000)
                    .ToListAsync(cancellationToken);
                foreach (var row in rows)
                {
                    sequence++;
                    row.Seq = sequence;
                    row.PrevHash = previousHash;
                    row.Hash = ComputeHash(previousHash, row, sequence);
                    previousHash = row.Hash;
                }

                await context.SaveChangesAsync(cancellationToken);
                if (rows.Count < 5000)
                {
                    break;
                }
            }

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(cancellationToken);
            }
        }
        catch
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
            if (acquiredSqliteLock)
            {
                SqliteWriteLock.Release();
            }
        }
    }

    public async Task<AuditVerificationResult> VerifyAsync(
        HoneyMomDbContext context,
        CancellationToken cancellationToken)
    {
        await SealAsync(context, cancellationToken);
        var unsealed = await context.AuditLogs
            .AsNoTracking()
            .CountAsync(row => row.Seq == null, cancellationToken);
        var rows = await context.AuditLogs
            .AsNoTracking()
            .Where(row => row.Seq != null)
            .OrderBy(row => row.Seq)
            .ToListAsync(cancellationToken);

        var expectedSequence = 1;
        var previousHash = "";
        var checkedCount = 0;
        foreach (var row in rows)
        {
            if (row.Seq != expectedSequence)
            {
                return new AuditVerificationResult(
                    false,
                    checkedCount,
                    unsealed,
                    new AuditProblem(
                        expectedSequence,
                        null,
                        $"缺第 {expectedSequence} 条（下一条是第 {row.Seq} 条）：有记录被删除"));
            }

            if (!string.Equals(row.PrevHash ?? "", previousHash, StringComparison.Ordinal))
            {
                return new AuditVerificationResult(
                    false,
                    checkedCount,
                    unsealed,
                    new AuditProblem(row.Seq, row.Id, "前序哈希对不上：前面的记录被改过或删过"));
            }

            var expectedHash = ComputeHash(previousHash, row, row.Seq!.Value);
            if (!string.Equals(expectedHash, row.Hash, StringComparison.Ordinal))
            {
                return new AuditVerificationResult(
                    false,
                    checkedCount,
                    unsealed,
                    new AuditProblem(row.Seq, row.Id, "内容哈希不符：这条记录封存后被改过"));
            }

            previousHash = row.Hash ?? "";
            expectedSequence++;
            checkedCount++;
        }

        return new AuditVerificationResult(true, checkedCount, unsealed, null);
    }

    private static string? DefaultTarget(AuditEntry entry) =>
        entry.Entity is null ? "" : $"{entry.Entity}:{entry.EntityId}";

    private static object? Redact(object? value)
    {
        if (Normalize(value) is not IDictionary dictionary)
        {
            return value;
        }

        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry item in dictionary)
        {
            var key = Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? "";
            result[key] = SensitiveFields.Contains(key)
                ? new[] { "***", "***" }
                : Normalize(item.Value);
        }

        return result;
    }

    private static string ComputeHash(string previousHash, AuditLogRow row, int sequence)
    {
        var canonical = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["time"] = FormatDateTime(row.Time),
            ["user"] = row.User,
            ["kind"] = row.Kind,
            ["target"] = row.Target,
            ["entity"] = row.Entity,
            ["entity_id"] = row.EntityId,
            ["action"] = row.Action,
            ["diff"] = ParseJson(row.Diff),
            ["detail"] = ParseJson(row.Detail),
            ["source"] = row.Source,
            ["ip"] = row.Ip,
            ["seq"] = sequence
        };
        var payload = JsonSerializer.Serialize(canonical, JsonOptions);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(previousHash + payload));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string? FormatDateTime(DateTime? value) =>
        value?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static object? ParseJson(string? value)
    {
        if (value is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(value);
        return NormalizeJsonElement(document.RootElement);
    }

    private static string SerializeJson(object? value) =>
        JsonSerializer.Serialize(Normalize(value), JsonOptions);

    private static object? Normalize(object? value) =>
        value switch
        {
            null => null,
            DateTime dateTime => FormatDateTime(dateTime),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            JsonElement element => NormalizeJsonElement(element),
            IDictionary dictionary => NormalizeDictionary(dictionary),
            IEnumerable enumerable when value is not string => enumerable.Cast<object?>()
                .Select(Normalize)
                .ToArray(),
            _ => value
        };

    private static object? NormalizeJsonElement(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Object => new SortedDictionary<string, object?>(
                element.EnumerateObject().ToDictionary(
                    property => property.Name,
                    property => NormalizeJsonElement(property.Value),
                    StringComparer.Ordinal),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray()
                .Select(NormalizeJsonElement)
                .ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var number) => number,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };

    private static object NormalizeDictionary(IDictionary dictionary)
    {
        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry item in dictionary)
        {
            result[Convert.ToString(item.Key, CultureInfo.InvariantCulture) ?? ""] = Normalize(item.Value);
        }

        return result;
    }
}
