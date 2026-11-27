using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using HoneyMOM.Application.Labeling;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Labeling;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Labeling;

public sealed class WorkOrderReleaseLabelServiceTests
{
    [Fact]
    public async Task ReleaseRulesRespectFiltersAndDeviceState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync("""{"elements":[]}""");
        var browserPrinterId = await database.AddPrinterAsync("browser");
        var disabledPrinterId = await database.AddPrinterAsync("zpl", enabled: false);
        var disabledMarkerId = await database.AddMarkerAsync(enabled: false);
        var differentMaterial = checked((int)database.WorkOrder.ProductId!.Value + 1);

        await database.AddRuleAsync(templateId, printerId: browserPrinterId, customer: "ACME", materialId: checked((int)database.WorkOrder.ProductId.Value));
        await database.AddRuleAsync(templateId, printerId: browserPrinterId, customer: "OTHER");
        await database.AddRuleAsync(templateId, printerId: browserPrinterId, materialId: differentMaterial);
        await database.AddRuleAsync(templateId, printerId: disabledPrinterId);
        await database.AddRuleAsync(templateId, markerId: disabledMarkerId);
        await database.AddRuleAsync(templateId, printerId: browserPrinterId, enabled: false);

        var count = await database.QueueAsync();

        Assert.Equal(1, count);
        var job = Assert.Single(await database.ReadJobsAsync());
        Assert.Equal("browser", job["kind"]);
        Assert.Equal(browserPrinterId, Convert.ToInt32(job["printer"]));
    }

    [Fact]
    public async Task ReleaseSkipsMissingOrObsoleteTemplate()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.AddRuleAsync(templateId: 999_999);
        var obsoleteTemplateId = await database.AddTemplateAsync("""{"elements":[]}""", status: "obsolete");
        await database.AddRuleAsync(obsoleteTemplateId);

        var count = await database.QueueAsync();

        Assert.Equal(0, count);
        Assert.Empty(await database.ReadJobsAsync());
    }

    [Fact]
    public async Task BrowserRuleCreatesCompletedJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync("""{"elements":[]}""");
        await database.AddRuleAsync(templateId, copies: 501);

        var count = await database.QueueAsync();

        Assert.Equal(1, count);
        var job = Assert.Single(await database.ReadJobsAsync());
        AssertJobCode(job);
        Assert.Equal("browser", job["kind"]);
        Assert.Equal(templateId, Convert.ToInt32(job["template"]));
        Assert.Null(job["printer"]);
        Assert.Null(job["marker"]);
        Assert.Equal("wo:WO-42", job["target"]);
        Assert.Equal("WO-42", job["ref"]);
        Assert.Equal(500, Convert.ToInt32(job["copies"]));
        Assert.Equal("done", job["status"]);
        Assert.Equal(0, Convert.ToInt32(job["attempts"]));
        Assert.Equal(false, job["reprint"]);
        Assert.Null(job["reason"]);
        Assert.Equal("Label Operator", job["requested_by"]);
        Assert.Equal("wo_release", job["event"]);
        Assert.Null(job["next_try"]);
        Assert.Equal(database.Now.DateTime, job["sent_at"]);

        using var data = JsonDocument.Parse(Assert.IsType<string>(job["data"]));
        AssertWorkOrderData(data.RootElement, database);

        using var log = JsonDocument.Parse(Assert.IsType<string>(job["log"]));
        var entry = Assert.Single(log.RootElement.EnumerateArray().ToArray());
        Assert.Equal("2026-11-25 04:10:57", entry.GetProperty("t").GetString());
        Assert.True(entry.GetProperty("ok").GetBoolean());
        Assert.Equal("浏览器打印（PDF）", entry.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task UnsupportedKnownTemplateUsageDoesNotQueueJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync("""{"elements":[]}""", usage: "part");
        await database.AddRuleAsync(templateId);

        await Assert.ThrowsAsync<NotSupportedException>(() => database.QueueAsync());

        Assert.Empty(await database.ReadJobsAsync());
    }

    [Fact]
    public async Task UnknownTemplateUsageFallsBackToWorkOrderData()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync("""{"elements":[]}""", usage: "unknown");
        await database.AddRuleAsync(templateId);

        Assert.Equal(1, await database.QueueAsync());

        var job = Assert.Single(await database.ReadJobsAsync());
        using var data = JsonDocument.Parse(Assert.IsType<string>(job["data"]));
        AssertWorkOrderData(data.RootElement, database);
    }

    [Fact]
    public async Task PrinterRuleCreatesQueuedJobWithRenderedPayload()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync(
            """{"elements":[{"type":"text","x":0,"y":0,"h":4,"text":"{{code}}"}]}""",
            widthMm: 10,
            heightMm: 5);
        var printerId = await database.AddPrinterAsync("zpl", dpi: 203, darkness: 8);
        await database.AddRuleAsync(templateId, printerId: printerId, copies: 2);

        var count = await database.QueueAsync();

        Assert.Equal(1, count);
        var job = Assert.Single(await database.ReadJobsAsync());
        AssertJobCode(job);
        Assert.Equal("print", job["kind"]);
        Assert.Equal(templateId, Convert.ToInt32(job["template"]));
        Assert.Equal(printerId, Convert.ToInt32(job["printer"]));
        Assert.Null(job["marker"]);
        Assert.Equal("wo:WO-42", job["target"]);
        Assert.Equal("WO-42", job["ref"]);
        Assert.Equal(2, Convert.ToInt32(job["copies"]));
        Assert.Equal("queued", job["status"]);
        Assert.Equal(0, Convert.ToInt32(job["attempts"]));
        Assert.Equal("Label Operator", job["requested_by"]);
        Assert.Equal("wo_release", job["event"]);
        Assert.Equal(database.Now.DateTime, job["next_try"]);
        Assert.Null(job["sent_at"]);
        Assert.Equal(
            "^XA\n^CI28\n~SD08\n^PW80\n^LL40\n^LH0,0\n^FO0,0^A0N,32,32^FDWO-42^FS\n^PQ2\n^XZ\n",
            job["payload"]);

        using var data = JsonDocument.Parse(Assert.IsType<string>(job["data"]));
        AssertWorkOrderData(data.RootElement, database);
        using var log = JsonDocument.Parse(Assert.IsType<string>(job["log"]));
        Assert.Empty(log.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task MarkerRuleCreatesQueuedJobWithExpandedCommand()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync(
            """{"elements":[{"type":"barcode","x":0,"y":0,"w":5,"h":2,"data":"{{code}}","symbology":"code128"}]}""");
        var markerId = await database.AddMarkerAsync("MARK {job} {data} {fields.product}\\r\\n");
        await database.AddRuleAsync(templateId, markerId: markerId, copies: 7);

        var count = await database.QueueAsync();

        Assert.Equal(1, count);
        var job = Assert.Single(await database.ReadJobsAsync());
        var jobCode = Assert.IsType<string>(job["code"]);
        Assert.Matches(new Regex("^PJ261125[0-9]{4}$", RegexOptions.CultureInvariant), jobCode);
        Assert.Equal("mark", job["kind"]);
        Assert.Equal(templateId, Convert.ToInt32(job["template"]));
        Assert.Null(job["printer"]);
        Assert.Equal(markerId, Convert.ToInt32(job["marker"]));
        Assert.Equal("wo:WO-42", job["target"]);
        Assert.Equal("WO-42", job["ref"]);
        Assert.Equal(1, Convert.ToInt32(job["copies"]));
        Assert.Equal("queued", job["status"]);
        Assert.Equal(0, Convert.ToInt32(job["attempts"]));
        Assert.Equal("Label Operator", job["requested_by"]);
        Assert.Equal("wo_release", job["event"]);
        Assert.Equal(database.Now.DateTime, job["next_try"]);
        Assert.Null(job["sent_at"]);
        Assert.Equal($"MARK {jobCode} WO-42 MAT-01 Widget\r\n", job["payload"]);

        using var data = JsonDocument.Parse(Assert.IsType<string>(job["data"]));
        AssertWorkOrderData(data.RootElement, database);
        using var log = JsonDocument.Parse(Assert.IsType<string>(job["log"]));
        Assert.Empty(log.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task ReleaseRuleRejectsInvalidTemplateWithoutPersistingJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync(
            """{"elements":[{"type":"text","x":"not-a-number","y":0,"h":4,"text":"{{code}}"}]}""");
        var printerId = await database.AddPrinterAsync("zpl");
        await database.AddRuleAsync(templateId, printerId: printerId);

        await Assert.ThrowsAsync<FormatException>(() => database.QueueAsync());

        Assert.Empty(await database.ReadJobsAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"elements\":{}}")]
    [InlineData("null")]
    public async Task ReleaseRuleRejectsInvalidLayoutShapeWithoutPersistingJob(string layout)
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync(layout);
        var printerId = await database.AddPrinterAsync("zpl");
        await database.AddRuleAsync(templateId, printerId: printerId);

        await Assert.ThrowsAsync<InvalidDataException>(() => database.QueueAsync());

        Assert.Empty(await database.ReadJobsAsync());
    }

    [Fact]
    public async Task ReleaseRuleRendersTopLevelElementArrayLayout()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync(
            """[{"type":"text","x":0,"y":0,"h":4,"text":"{{code}}"}]""");
        var printerId = await database.AddPrinterAsync("zpl");
        await database.AddRuleAsync(templateId, printerId: printerId);

        Assert.Equal(1, await database.QueueAsync());

        var job = Assert.Single(await database.ReadJobsAsync());
        Assert.Equal(
            "^XA\n^CI28\n^PW80\n^LL40\n^LH0,0\n^FO0,0^A0N,32,32^FDWO-42^FS\n^PQ1\n^XZ\n",
            job["payload"]);
    }

    [Fact]
    public async Task QueueUsesCallerOwnedTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var templateId = await database.AddTemplateAsync("""{"elements":[]}""");
        await database.AddRuleAsync(templateId);
        await using var transaction = await database.Context.Database.BeginTransactionAsync();

        var count = await database.Service.QueueAsync(database.WorkOrder, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(transaction.TransactionId, database.Context.Database.CurrentTransaction?.TransactionId);
        Assert.Single(await database.ReadJobsAsync());

        await transaction.RollbackAsync();

        Assert.Empty(await database.ReadJobsAsync());
    }

    [Fact]
    public async Task ReleaseQueueDoesNotDeadlockWithConcurrentAutoCodedEntityCreate()
    {
        using var directory = new TemporaryDirectory();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory.Path, "release.db"),
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        await using var database = await TestDatabase.CreateAsync(connectionString);
        var templateId = await database.AddTemplateAsync("""{"elements":[]}""");
        await database.AddRuleAsync(templateId);
        await using var releaseTransaction = await database.Context.Database.BeginTransactionAsync();
        await database.Store.InsertAsync(
            "setting",
            new Dictionary<string, object?>
            {
                ["key"] = "release-lock",
                ["value"] = "held"
            },
            CancellationToken.None);

        var connectionOpened = new ConnectionOpenedSignal();
        var catalog = new SchemaSnapshotCatalog();
        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(connectionOpened)
            .Options;
        await using var createContext = new HoneyMomDbContext(options);
        var createStore = new DynamicEntityStore(createContext, catalog);
        var createService = new EntityCommandService(
            createContext,
            catalog,
            createStore,
            new EntityQueryService(createContext, catalog),
            new AuditService());
        var createTask = Task.Run(() => createService.CreateAsync(
                "work_order",
                new Dictionary<string, JsonElement>
                {
                    ["product"] = JsonSerializer.SerializeToElement(database.WorkOrder.ProductId),
                    ["qty"] = JsonSerializer.SerializeToElement(1)
                },
                new CallerPrincipal(1, "concurrent", "Concurrent", []),
                CancellationToken.None));

        await connectionOpened.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedJobs = await database.Service.QueueAsync(
            database.WorkOrder,
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        await releaseTransaction.CommitAsync();
        var createdWorkOrderId = await createTask.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, queuedJobs);
        Assert.True(createdWorkOrderId > 0);
    }

    private static void AssertJobCode(IReadOnlyDictionary<string, object?> job)
    {
        var code = Assert.IsType<string>(job["code"]);
        Assert.Matches(new Regex("^PJ261125[0-9]{4}$", RegexOptions.CultureInvariant), code);
    }

    private static void AssertWorkOrderData(JsonElement data, TestDatabase database)
    {
        Assert.Equal("WO-42", data.GetProperty("code").GetString());
        Assert.Equal("MAT-01 Widget", data.GetProperty("product").GetString());
        Assert.Equal(12, data.GetProperty("qty").GetInt32());
        Assert.Equal("2026-11-30", data.GetProperty("due").GetString());
        Assert.Equal("ACME", data.GetProperty("customer").GetString());
        Assert.Equal("HoneyMeta", data.GetProperty("company").GetString());
        Assert.Equal("2026-11-25", data.GetProperty("date").GetString());
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DynamicEntityStore _store;

        private TestDatabase(
            SqliteConnection connection,
            HoneyMomDbContext context,
            DynamicEntityStore store,
            WorkOrderReleaseLabelService service,
            WorkOrderReleaseLabelContext workOrder)
        {
            _connection = connection;
            Context = context;
            _store = store;
            Service = service;
            WorkOrder = workOrder;
        }

        public HoneyMomDbContext Context { get; }
        public WorkOrderReleaseLabelService Service { get; }
        public WorkOrderReleaseLabelContext WorkOrder { get; }
        public DynamicEntityStore Store => _store;
        public DateTimeOffset Now => new(2026, 11, 25, 4, 10, 57, TimeSpan.Zero);

        public static async Task<TestDatabase> CreateAsync(string? connectionString = null)
        {
            var connection = new SqliteConnection(connectionString ?? "Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new HoneyMomDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var store = new DynamicEntityStore(context);
            var productId = checked((int)await store.InsertAsync(
                "material",
                new Dictionary<string, object?>
                {
                    ["code"] = "MAT-01",
                    ["name"] = "Widget"
                },
                CancellationToken.None));
            var workOrderId = await store.InsertAsync(
                "work_order",
                new Dictionary<string, object?>
                {
                    ["code"] = "WO-42",
                    ["product"] = productId,
                    ["qty"] = 12,
                    ["due"] = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Unspecified),
                    ["customer"] = "ACME"
                },
                CancellationToken.None);
            await store.InsertAsync(
                "setting",
                new Dictionary<string, object?>
                {
                    ["key"] = "company",
                    ["value"] = "HoneyMeta"
                },
                CancellationToken.None);

            var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 25, 4, 10, 57, TimeSpan.Zero));
            var service = new WorkOrderReleaseLabelService(
                context,
                store,
                new SchemaSnapshotCatalog(),
                clock);
            var workOrder = new WorkOrderReleaseLabelContext(
                workOrderId,
                "WO-42",
                productId,
                "ACME",
                "Label Operator");
            return new TestDatabase(connection, context, store, service, workOrder);
        }

        public async Task<int> AddTemplateAsync(
            string layout,
            string status = "active",
            double widthMm = 10,
            double heightMm = 5,
            string usage = "wo")
        {
            var id = await _store.InsertAsync(
                "label_template",
                new Dictionary<string, object?>
                {
                    ["code"] = $"TPL-{Guid.NewGuid():N}",
                    ["name"] = "Work-order label",
                    ["usage"] = usage,
                    ["width_mm"] = widthMm,
                    ["height_mm"] = heightMm,
                    ["layout"] = layout,
                    ["status"] = status
                },
                CancellationToken.None);
            return checked((int)id);
        }

        public async Task<int> AddPrinterAsync(
            string kind,
            bool enabled = true,
            int dpi = 203,
            int? darkness = null)
        {
            var id = await _store.InsertAsync(
                "printer",
                new Dictionary<string, object?>
                {
                    ["code"] = $"PRN-{Guid.NewGuid():N}",
                    ["name"] = "Test printer",
                    ["kind"] = kind,
                    ["dpi"] = dpi,
                    ["darkness"] = darkness,
                    ["enabled"] = enabled
                },
                CancellationToken.None);
            return checked((int)id);
        }

        public async Task<int> AddMarkerAsync(
            string commandTemplate = "MARK {job} {data}\\r\\n",
            bool enabled = true)
        {
            var id = await _store.InsertAsync(
                "marker",
                new Dictionary<string, object?>
                {
                    ["code"] = $"MRK-{Guid.NewGuid():N}",
                    ["name"] = "Test marker",
                    ["mode"] = "tcp",
                    ["cmd_template"] = commandTemplate,
                    ["symbology"] = "datamatrix",
                    ["enabled"] = enabled
                },
                CancellationToken.None);
            return checked((int)id);
        }

        public async Task AddRuleAsync(
            int? templateId,
            int? printerId = null,
            int? markerId = null,
            int? copies = 1,
            string? customer = null,
            int? materialId = null,
            bool enabled = true)
        {
            await _store.InsertAsync(
                "label_rule",
                new Dictionary<string, object?>
                {
                    ["event"] = "wo_release",
                    ["template"] = templateId,
                    ["printer"] = printerId,
                    ["marker"] = markerId,
                    ["copies"] = copies,
                    ["customer"] = customer,
                    ["material"] = materialId,
                    ["enabled"] = enabled
                },
                CancellationToken.None);
        }

        public async Task<int> QueueAsync()
        {
            await using var transaction = await Context.Database.BeginTransactionAsync();
            var count = await Service.QueueAsync(WorkOrder, CancellationToken.None);
            await transaction.CommitAsync();
            return count;
        }

        public Task<List<Dictionary<string, object?>>> ReadJobsAsync() =>
            Context.Set<Dictionary<string, object?>>("print_job")
                .AsNoTracking()
                .ToListAsync();

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class ConnectionOpenedSignal : DbConnectionInterceptor
    {
        private readonly TaskCompletionSource _connectionOpened =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(TimeSpan timeout) => _connectionOpened.Task.WaitAsync(timeout);

        public override Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            _connectionOpened.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"honeymom-release-lock-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
