using System.Text.Json;
using HoneyMOM.Infrastructure.Labeling;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Labeling;

public sealed class LabelQueueProviderTests
{
    private const string Payload = "^XA\n^FDWO-42^FS\n^XZ\n";

    [Fact]
    public async Task QueuedJobRetryStateRoundTripsOnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connection)
            .Options;

        await VerifyRetryStateRoundTripAsync(options);
    }

    [PostgreSqlFact]
    public async Task QueuedJobRetryStateRoundTripsOnPostgreSql()
    {
        var url = Environment.GetEnvironmentVariable("FC_PG")!;
        var connectionOptions = DatabaseUrlParser.Parse(url, Path.GetTempPath());
        var parsedUrl = new Uri(url.Replace(
            "postgresql+psycopg://",
            "postgresql://",
            StringComparison.Ordinal));
        var adminConnectionString = new NpgsqlConnectionStringBuilder(
            connectionOptions.ConnectionString)
        {
            Database = string.IsNullOrEmpty(parsedUrl.AbsolutePath.Trim('/'))
                ? "postgres"
                : Uri.UnescapeDataString(parsedUrl.AbsolutePath.Trim('/'))
        };
        var schema = $"hm_label_{Guid.NewGuid():N}";

        await using var adminConnection = new NpgsqlConnection(adminConnectionString.ConnectionString);
        await adminConnection.OpenAsync();
        await using (var createSchema = new NpgsqlCommand(
            $"CREATE SCHEMA \"{schema}\"",
            adminConnection))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var testConnectionString = new NpgsqlConnectionStringBuilder(
                adminConnectionString.ConnectionString)
            {
                SearchPath = schema
            };
            await using var connection = new NpgsqlConnection(testConnectionString.ConnectionString);
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseNpgsql(connection)
                .Options;

            await VerifyRetryStateRoundTripAsync(options);
        }
        finally
        {
            await using var dropSchema = new NpgsqlCommand(
                $"DROP SCHEMA \"{schema}\" CASCADE",
                adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyRetryStateRoundTripAsync(
        DbContextOptions<HoneyMomDbContext> options)
    {
        var clock = new FixedTimeProvider(
            new DateTimeOffset(2026, 11, 25, 4, 10, 57, TimeSpan.Zero));
        long jobId;
        long printerId;

        await using (var context = new HoneyMomDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
            var store = new DynamicEntityStore(context);
            printerId = await store.InsertAsync(
                "printer",
                new Dictionary<string, object?>
                {
                    ["code"] = "PRN-LABEL-ROUNDTRIP",
                    ["name"] = "Loopback test printer",
                    ["kind"] = "zpl",
                    ["host"] = "127.0.0.1",
                    ["port"] = 9100,
                    ["enabled"] = true
                },
                CancellationToken.None);
            jobId = await store.InsertAsync(
                "print_job",
                new Dictionary<string, object?>
                {
                    ["code"] = "PJ2611250001",
                    ["kind"] = "print",
                    ["printer"] = checked((int)printerId),
                    ["target"] = "wo:WO-42",
                    ["ref"] = "WO-42",
                    ["data"] = """{"code":"WO-42","qty":2}""",
                    ["payload"] = Payload,
                    ["copies"] = 1,
                    ["status"] = "queued",
                    ["attempts"] = 0,
                    ["last_error"] = null,
                    ["reprint"] = false,
                    ["reason"] = null,
                    ["requested_by"] = "Test Operator",
                    ["event"] = "wo_release",
                    ["next_try"] = clock.Now.DateTime,
                    ["sent_at"] = null,
                    ["log"] = "[]",
                    ["created_at"] = clock.Now.DateTime,
                    ["updated_at"] = clock.Now.DateTime
                },
                CancellationToken.None);

            var processor = new LabelJobProcessor(
                context,
                store,
                new FailingTransport(),
                clock);
            var result = await processor.ProcessJobAsync(jobId, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("queued", result["status"]);
            Assert.Equal(1, Convert.ToInt32(result["attempts"]));
        }

        await using var verifyContext = new HoneyMomDbContext(options);
        var verifyStore = new DynamicEntityStore(verifyContext);
        var job = await verifyStore.GetAsync("print_job", jobId, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(Payload, job["payload"]);
        Assert.Equal("queued", job["status"]);
        Assert.Equal(1, Convert.ToInt32(job["attempts"]));
        Assert.Equal(
            clock.Now.AddSeconds(5).DateTime,
            Assert.IsType<DateTime>(job["next_try"]));
        Assert.Contains("TimeoutException", Assert.IsType<string>(job["last_error"]));

        using var data = JsonDocument.Parse(Assert.IsType<string>(job["data"]));
        Assert.Equal("WO-42", data.RootElement.GetProperty("code").GetString());
        using var log = JsonDocument.Parse(Assert.IsType<string>(job["log"]));
        var entry = Assert.Single(log.RootElement.EnumerateArray().ToArray());
        Assert.False(entry.GetProperty("ok").GetBoolean());
        Assert.Contains("device timed out", entry.GetProperty("msg").GetString());

        var printer = await verifyStore.GetAsync("printer", printerId, CancellationToken.None);
        Assert.NotNull(printer);
        Assert.Equal("error", printer["status"]);
        Assert.Contains("device timed out", Assert.IsType<string>(printer["last_error"]));
    }

    private sealed class FailingTransport : IPrintDeviceTransport
    {
        public Task SendPrinterAsync(
            string host,
            int port,
            ReadOnlyMemory<byte> payload,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromException(new TimeoutException("device timed out"));

        public Task<string> SendMarkerAsync(
            string host,
            int port,
            ReadOnlyMemory<byte> command,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("This test only processes printer jobs.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
