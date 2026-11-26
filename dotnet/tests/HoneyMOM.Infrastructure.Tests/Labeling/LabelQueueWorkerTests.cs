using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HoneyMOM.Infrastructure.Labeling;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Labeling;

public sealed class LabelQueueWorkerTests
{
    [Fact]
    public async Task QueuedJobSendsAndRecordsAcknowledgment()
    {
        var transport = new FakeTransport();
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var jobId = await database.AddJobAsync(printerId: printerId, payload: "label-payload");

        var result = await database.Processor.ProcessJobAsync(jobId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("done", result["status"]);
        Assert.Equal(1, Convert.ToInt32(result["attempts"]));
        Assert.Null(result["next_try"]);
        Assert.Equal(database.Clock.Now.DateTime, result["sent_at"]);
        Assert.Null(result["last_error"]);
        Assert.Equal(9100, Assert.Single(transport.PrinterEndpoints).Port);
        Assert.Equal(
            Encoding.UTF8.GetBytes("label-payload"),
            Assert.Single(transport.PrinterPayloads));

        var log = ReadLog(result);
        var entry = Assert.Single(log);
        Assert.True(entry.GetProperty("ok").GetBoolean());
        Assert.InRange(entry.GetProperty("msg").GetString()!.Length, 1, 300);

        var printer = await database.ReadDeviceAsync("printer", printerId);
        Assert.Equal("ok", printer!["status"]);
        Assert.Equal(database.Clock.Now.DateTime, printer["last_ok"]);
        Assert.Null(printer["last_error"]);
    }

    [Fact]
    public async Task JobIsMarkedSendingBeforeNetworkAndNetworkRunsOutsideTransaction()
    {
        var transport = new FakeTransport();
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var jobId = await database.AddJobAsync(printerId: printerId);
        string? statusAtSend = null;
        var transactionAtSend = true;
        transport.PrinterSender = async (_, _, _, _, _) =>
        {
            statusAtSend = Assert.IsType<string>((await database.ReadJobAsync(jobId))["status"]);
            transactionAtSend = database.Context.Database.CurrentTransaction is not null;
        };

        await database.Processor.ProcessJobAsync(jobId, CancellationToken.None);

        Assert.Equal("sending", statusAtSend);
        Assert.False(transactionAtSend);
    }

    [Fact]
    public async Task BrowserJobsAreNeverSent()
    {
        var transport = new FakeTransport();
        await using var database = await TestDatabase.CreateAsync(transport);
        var jobId = await database.AddJobAsync(kind: "browser", printerId: null);

        var result = await database.Processor.ProcessJobAsync(jobId, CancellationToken.None);

        Assert.Null(result);
        var job = await database.ReadJobAsync(jobId);
        Assert.Equal("queued", job["status"]);
        Assert.Empty(transport.PrinterPayloads);
        Assert.Empty(transport.MarkerCommands);
    }

    [Fact]
    public async Task FailedAttemptUpdatesDeviceAndJob()
    {
        var transport = new FakeTransport
        {
            PrinterSender = static (_, _, _, _, _) =>
                Task.FromException(new SocketException((int)SocketError.ConnectionRefused))
        };
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var jobId = await database.AddJobAsync(printerId: printerId);

        var result = await database.Processor.ProcessJobAsync(jobId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("queued", result["status"]);
        Assert.Equal(1, Convert.ToInt32(result["attempts"]));
        Assert.Equal(database.Clock.Now.AddSeconds(5).DateTime, result["next_try"]);
        Assert.NotNull(result["last_error"]);
        Assert.False(Assert.Single(ReadLog(result)).GetProperty("ok").GetBoolean());

        var printer = await database.ReadDeviceAsync("printer", printerId);
        Assert.Equal("error", printer!["status"]);
        Assert.NotNull(printer["last_error"]);
        Assert.Null(printer["last_ok"]);
    }

    [Fact]
    public async Task RetriesUseReferenceDelaysAndThenFail()
    {
        var transport = new FakeTransport
        {
            PrinterSender = static (_, _, _, _, _) =>
                Task.FromException(new IOException(new string('x', 400)))
        };
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var jobId = await database.AddJobAsync(printerId: printerId);
        var retryDelays = new[] { 5, 20, 60 };

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var result = await database.Processor.ProcessJobAsync(jobId, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(attempt, Convert.ToInt32(result["attempts"]));
            if (attempt <= retryDelays.Length)
            {
                Assert.Equal("queued", result["status"]);
                Assert.Equal(
                    database.Clock.Now.AddSeconds(retryDelays[attempt - 1]).DateTime,
                    result["next_try"]);
                database.Clock.Advance(TimeSpan.FromSeconds(retryDelays[attempt - 1]));
            }
            else
            {
                Assert.Equal("failed", result["status"]);
                Assert.Null(result["next_try"]);
            }
        }

        var job = await database.ReadJobAsync(jobId);
        var log = ReadLog(job);
        Assert.Equal(4, log.Length);
        Assert.All(log, entry => Assert.InRange(entry.GetProperty("msg").GetString()!.Length, 1, 300));
        Assert.True(Assert.IsType<string>(job["last_error"]).Length <= 190);

        var printer = await database.ReadDeviceAsync("printer", printerId);
        Assert.Equal("error", printer!["status"]);
        Assert.True(Assert.IsType<string>(printer["last_error"]).Length <= 190);
    }

    [Fact]
    public async Task SendingJobIsRecoveredAndMayBeDeliveredAgain()
    {
        var transport = new FakeTransport();
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var jobId = await database.AddJobAsync(
            printerId: printerId,
            status: "sending",
            payload: "already-accepted-before-restart");
        using var services = database.CreateServiceProvider(transport);
        var worker = new LabelQueueWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            database.Clock);

        await worker.StartAsync(CancellationToken.None);
        await transport.WaitForPrinterSendsAsync();
        await database.WaitForJobStatusAsync(jobId, "done");
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(
            Encoding.UTF8.GetBytes("already-accepted-before-restart"),
            Assert.Single(transport.PrinterPayloads));
        var result = await database.ReadJobAsync(jobId);
        Assert.Equal("done", result["status"]);
        Assert.Equal(1, Convert.ToInt32(result["attempts"]));
    }

    [Fact]
    public async Task QueuedJobsAreClaimedInIdOrder()
    {
        var transport = new FakeTransport(expectedPrinterSends: 3);
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var expectedPayloads = new[] { "first", "second", "third" };
        var jobIds = new List<long>();
        foreach (var payload in expectedPayloads)
        {
            jobIds.Add(await database.AddJobAsync(printerId: printerId, payload: payload));
        }

        using var services = database.CreateServiceProvider(transport);
        var worker = new LabelQueueWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            database.Clock);

        await worker.StartAsync(CancellationToken.None);
        await transport.WaitForPrinterSendsAsync();
        foreach (var jobId in jobIds)
        {
            await database.WaitForJobStatusAsync(jobId, "done");
        }
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(
            expectedPayloads,
            transport.PrinterPayloads.Select(payload => Encoding.UTF8.GetString(payload)));
    }

    [Fact]
    public async Task MarkerAckAndErrorPatternsAreRespected()
    {
        var markerResponse = 0;
        var transport = new FakeTransport(expectedMarkerSends: 2)
        {
            MarkerSender = (_, _, _, _, _) => Task.FromResult(
                Interlocked.Increment(ref markerResponse) == 1 ? "OK" : "ERR OK")
        };
        await using var database = await TestDatabase.CreateAsync(transport);
        var markerId = await database.AddMarkerAsync(timeoutMs: 1250);
        var acknowledgedJobId = await database.AddJobAsync(kind: "mark", markerId: markerId);
        var rejectedJobId = await database.AddJobAsync(kind: "mark", markerId: markerId);

        var acknowledged = await database.Processor.ProcessJobAsync(
            acknowledgedJobId,
            CancellationToken.None);
        var rejected = await database.Processor.ProcessJobAsync(
            rejectedJobId,
            CancellationToken.None);

        Assert.Equal("done", acknowledged!["status"]);
        Assert.Equal("queued", rejected!["status"]);
        Assert.Equal(1, Convert.ToInt32(rejected["attempts"]));
        Assert.Contains("ERR", Assert.IsType<string>(rejected["last_error"]), StringComparison.Ordinal);
        Assert.False(Assert.Single(ReadLog(rejected)).GetProperty("ok").GetBoolean());
        Assert.Equal(2, transport.MarkerCommands.Count);
        Assert.All(transport.MarkerEndpoints, endpoint => Assert.Equal(9000, endpoint.Port));
        Assert.All(transport.MarkerTimeouts, timeout => Assert.Equal(
            TimeSpan.FromMilliseconds(1250),
            timeout));

        var marker = await database.ReadDeviceAsync("marker", markerId);
        Assert.Equal("error", marker!["status"]);
        Assert.NotNull(marker["last_error"]);
    }

    [Fact]
    public async Task SimulatedMarkerUsesReferenceProtocolWithoutNetworkSend()
    {
        var transport = new FakeTransport();
        await using var database = await TestDatabase.CreateAsync(transport);
        var markerId = await database.AddMarkerAsync(mode: "sim");
        var successfulJobId = await database.AddJobAsync(
            kind: "mark",
            markerId: markerId,
            payload: "MARK SIM-1 WO-1\r\n");
        var failedJobId = await database.AddJobAsync(
            kind: "mark",
            markerId: markerId,
            payload: "MARK SIM-2 FAIL\r\n");

        var successfulJob = await database.Processor.ProcessJobAsync(
            successfulJobId,
            CancellationToken.None);
        var failedJob = await database.Processor.ProcessJobAsync(
            failedJobId,
            CancellationToken.None);

        Assert.Equal("done", successfulJob!["status"]);
        Assert.Contains("OK SIM-1", Assert.IsType<string>(
            Assert.Single(ReadLog(successfulJob)).GetProperty("msg").GetString()));
        Assert.Equal("queued", failedJob!["status"]);
        Assert.Contains("ERR SIM-2", Assert.IsType<string>(failedJob["last_error"]));
        Assert.Empty(transport.MarkerCommands);
    }

    [Fact]
    public async Task TransportTimeoutIsRetriedButShutdownCancellationPropagates()
    {
        var transport = new FakeTransport
        {
            PrinterSender = static (_, _, _, _, _) =>
                Task.FromException(new TimeoutException("device timed out"))
        };
        await using var database = await TestDatabase.CreateAsync(transport);
        var printerId = await database.AddPrinterAsync();
        var timeoutJobId = await database.AddJobAsync(printerId: printerId);

        var timeoutResult = await database.Processor.ProcessJobAsync(
            timeoutJobId,
            CancellationToken.None);

        Assert.Equal("queued", timeoutResult!["status"]);
        Assert.Equal(1, Convert.ToInt32(timeoutResult["attempts"]));
        Assert.Equal(database.Clock.Now.AddSeconds(5).DateTime, timeoutResult["next_try"]);

        var cancellationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.PrinterSender = async (_, _, _, _, cancellationToken) =>
        {
            cancellationStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var cancelledJobId = await database.AddJobAsync(printerId: printerId);
        var printerBeforeCancellation = await database.ReadDeviceAsync("printer", printerId);
        using var shutdown = new CancellationTokenSource();
        var processing = database.Processor.ProcessJobAsync(cancelledJobId, shutdown.Token);
        await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);

        var cancelled = await database.ReadJobAsync(cancelledJobId);
        Assert.Equal("sending", cancelled["status"]);
        Assert.Equal(0, Convert.ToInt32(cancelled["attempts"]));
        Assert.Empty(ReadLog(cancelled));
        var printer = await database.ReadDeviceAsync("printer", printerId);
        Assert.Equal(printerBeforeCancellation!["status"], printer!["status"]);
        Assert.Equal(printerBeforeCancellation["last_error"], printer["last_error"]);
    }

    private static JsonElement[] ReadLog(IReadOnlyDictionary<string, object?> job)
    {
        using var document = JsonDocument.Parse(Assert.IsType<string>(job["log"]));
        return document.RootElement.EnumerateArray()
            .Select(entry => entry.Clone())
            .ToArray();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<HoneyMomDbContext> _options;
        private readonly DynamicEntityStore _store;

        private TestDatabase(
            SqliteConnection connection,
            DbContextOptions<HoneyMomDbContext> options,
            HoneyMomDbContext context,
            DynamicEntityStore store,
            FakeTransport transport,
            MutableTimeProvider clock)
        {
            _connection = connection;
            _options = options;
            Context = context;
            _store = store;
            Transport = transport;
            Clock = clock;
            Processor = new LabelJobProcessor(context, store, transport, clock);
        }

        public HoneyMomDbContext Context { get; }
        public FakeTransport Transport { get; }
        public MutableTimeProvider Clock { get; }
        public LabelJobProcessor Processor { get; }

        public static async Task<TestDatabase> CreateAsync(FakeTransport transport)
        {
            var connectionString =
                $"Data Source=LabelQueue-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseSqlite(connectionString)
                .Options;
            var context = new HoneyMomDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var store = new DynamicEntityStore(context);
            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 11, 25, 4, 10, 57, TimeSpan.Zero));
            return new TestDatabase(connection, options, context, store, transport, clock);
        }

        public async Task<long> AddPrinterAsync()
        {
            return await _store.InsertAsync(
                "printer",
                new Dictionary<string, object?>
                {
                    ["code"] = $"PRN-{Guid.NewGuid():N}",
                    ["name"] = "Loopback test printer",
                    ["kind"] = "zpl",
                    ["host"] = "127.0.0.1",
                    ["port"] = null,
                    ["enabled"] = true
                },
                CancellationToken.None);
        }

        public async Task<long> AddMarkerAsync(
            int timeoutMs = 5000,
            string mode = "tcp",
            string host = "127.0.0.1")
        {
            return await _store.InsertAsync(
                "marker",
                new Dictionary<string, object?>
                {
                    ["code"] = $"MRK-{Guid.NewGuid():N}",
                    ["name"] = "Loopback test marker",
                    ["mode"] = mode,
                    ["host"] = host,
                    ["port"] = null,
                    ["ack"] = "OK",
                    ["err"] = "ERR|NG",
                    ["timeout_ms"] = timeoutMs,
                    ["enabled"] = true
                },
                CancellationToken.None);
        }

        public async Task<long> AddJobAsync(
            long? printerId = null,
            long? markerId = null,
            string kind = "print",
            string status = "queued",
            string payload = "test-payload")
        {
            var code = $"T4-{Guid.NewGuid():N}";
            return await _store.InsertAsync(
                "print_job",
                new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["kind"] = kind,
                    ["printer"] = printerId is null ? null : checked((int)printerId.Value),
                    ["marker"] = markerId is null ? null : checked((int)markerId.Value),
                    ["target"] = "test",
                    ["ref"] = code,
                    ["data"] = "{}",
                    ["payload"] = payload,
                    ["copies"] = 1,
                    ["status"] = status,
                    ["attempts"] = 0,
                    ["last_error"] = null,
                    ["reprint"] = false,
                    ["reason"] = null,
                    ["requested_by"] = "Test Operator",
                    ["event"] = "test",
                    ["next_try"] = status == "queued" ? Clock.Now.DateTime : null,
                    ["sent_at"] = null,
                    ["log"] = "[]",
                    ["created_at"] = Clock.Now.DateTime,
                    ["updated_at"] = Clock.Now.DateTime
                },
                CancellationToken.None);
        }


        public async Task<Dictionary<string, object?>> ReadJobAsync(long id) =>
            await Context.Set<Dictionary<string, object?>>("print_job")
                .AsNoTracking()
                .SingleAsync(row => EF.Property<int>(row, "id") == checked((int)id));

        public async Task<IReadOnlyDictionary<string, object?>?> ReadDeviceAsync(
            string entity,
            long id) =>
            await _store.GetAsync(entity, id, CancellationToken.None);

        public ServiceProvider CreateServiceProvider(FakeTransport transport) =>
            new ServiceCollection()
                .AddScoped(_ => new HoneyMomDbContext(_options))
                .AddScoped<DynamicEntityStore>()
                .AddScoped<ILabelJobProcessor>(services =>
                {
                    var context = services.GetRequiredService<HoneyMomDbContext>();
                    var store = services.GetRequiredService<DynamicEntityStore>();
                    return new LabelJobProcessor(context, store, transport, Clock);
                })
                .BuildServiceProvider();

        public async Task WaitForJobStatusAsync(long id, string status)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var job = await ReadJobAsync(id);
                if (string.Equals(job["status"] as string, status, StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class FakeTransport(int expectedPrinterSends = 1, int expectedMarkerSends = 1)
        : IPrintDeviceTransport
    {
        private readonly TaskCompletionSource _printerSendsCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _markerSendsCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _printerSendCount;
        private int _markerSendCount;

        public ConcurrentQueue<(string Host, int Port)> PrinterEndpoints { get; } = new();
        public ConcurrentQueue<byte[]> PrinterPayloads { get; } = new();
        public ConcurrentQueue<(string Host, int Port)> MarkerEndpoints { get; } = new();
        public ConcurrentQueue<byte[]> MarkerCommands { get; } = new();
        public ConcurrentQueue<TimeSpan> MarkerTimeouts { get; } = new();

        public Func<string, int, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task>
            PrinterSender { get; set; } = static (_, _, _, _, _) => Task.CompletedTask;

        public Func<string, int, ReadOnlyMemory<byte>, TimeSpan, CancellationToken, Task<string>>
            MarkerSender { get; set; } = static (_, _, _, _, _) => Task.FromResult("OK");

        public Task WaitForPrinterSendsAsync() =>
            _printerSendsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public Task WaitForMarkerSendsAsync() =>
            _markerSendsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public async Task SendPrinterAsync(
            string host,
            int port,
            ReadOnlyMemory<byte> payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            PrinterEndpoints.Enqueue((host, port));
            PrinterPayloads.Enqueue(payload.ToArray());
            try
            {
                await PrinterSender(host, port, payload, timeout, cancellationToken);
            }
            finally
            {
                if (Interlocked.Increment(ref _printerSendCount) >= expectedPrinterSends)
                {
                    _printerSendsCompleted.TrySetResult();
                }
            }
        }

        public async Task<string> SendMarkerAsync(
            string host,
            int port,
            ReadOnlyMemory<byte> command,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            MarkerEndpoints.Enqueue((host, port));
            MarkerCommands.Enqueue(command.ToArray());
            MarkerTimeouts.Enqueue(timeout);
            try
            {
                return await MarkerSender(host, port, command, timeout, cancellationToken);
            }
            finally
            {
                if (Interlocked.Increment(ref _markerSendCount) >= expectedMarkerSends)
                {
                    _markerSendsCompleted.TrySetResult();
                }
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public DateTimeOffset Now => _now;

        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
