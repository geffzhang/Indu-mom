using System.Text.Json;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Persistence;

public sealed class AuditServiceTests
{
    [Fact]
    public async Task AppendUsesPythonCanonicalHash()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = new AuditService();

        await service.AppendAsync(
            context,
            new AuditEntry(
                "create",
                "plant",
                1,
                new Dictionary<string, object?> { ["name"] = new[] { "旧", "新" } },
                User: "admin",
                Target: "plant:1",
                Source: "ui",
                Time: new DateTime(2024, 1, 2, 3, 4, 5)),
            CancellationToken.None);
        await service.SealAsync(context, CancellationToken.None);

        var row = await context.AuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("6b797e5d97470c0b91c260418309b572122f4679b614135b8817c13cbc0fe422", row.Hash);
    }

    [Fact]
    public async Task SealSequencesRowsById()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = new AuditService();
        await service.AppendAsync(context, new AuditEntry("first", User: "admin"), CancellationToken.None);
        await service.AppendAsync(context, new AuditEntry("second", User: "admin"), CancellationToken.None);

        await service.SealAsync(context, CancellationToken.None);

        var rows = await context.AuditLogs.AsNoTracking().OrderBy(row => row.Seq).ToListAsync();
        Assert.Equal([1, 2], rows.Select(row => row.Seq).ToArray());
        Assert.Equal(rows[0].Hash, rows[1].PrevHash);
    }

    [Fact]
    public async Task RollbackRemovesBusinessAndAuditWrites()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = new AuditService();
        await using var transaction = await context.Database.BeginTransactionAsync();

        await new DynamicEntityStore(context).InsertAsync(
            "plant",
            new Dictionary<string, object?> { ["code"] = "ROLLBACK", ["name"] = "Rollback" },
            CancellationToken.None);
        await service.AppendAsync(context, new AuditEntry("create", "plant", 1), CancellationToken.None);
        await transaction.RollbackAsync();

        Assert.Equal(0, await context.Set<Dictionary<string, object?>>("plant").CountAsync());
        Assert.Empty(await context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSealsProduceContiguousSequence()
    {
        using var database = new TemporaryDatabase();
        await using (var seed = database.CreateContext())
        {
            await seed.Database.EnsureCreatedAsync();
            seed.AuditLogs.AddRange(Enumerable.Range(0, 20)
                .Select(index => new AuditLogRow { Kind = $"event-{index}", Time = DateTime.Now }));
            await seed.SaveChangesAsync();
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        await Task.WhenAll(
            new AuditService().SealAsync(first, CancellationToken.None),
            new AuditService().SealAsync(second, CancellationToken.None));

        await using var verify = database.CreateContext();
        var rows = await verify.AuditLogs.AsNoTracking().OrderBy(row => row.Seq).ToListAsync();
        Assert.Equal(Enumerable.Range(1, 20), rows.Select(row => row.Seq!.Value));
        Assert.True((await new AuditService().VerifyAsync(verify, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task VerifyDetectsTamperedPayload()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = new AuditService();
        await service.AppendAsync(context, new AuditEntry("create", "plant", 1), CancellationToken.None);
        await service.SealAsync(context, CancellationToken.None);
        await context.Database.ExecuteSqlRawAsync("UPDATE audit_log SET kind = 'tampered' WHERE seq = 1");

        var result = await service.VerifyAsync(context, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(0, result.Checked);
        Assert.Equal(1, result.Problem?.Sequence);
    }

    [Fact]
    public async Task SensitiveDiffValuesAreRedacted()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = new AuditService();
        await service.AppendAsync(
            context,
            new AuditEntry(
                "update",
                "sys_user",
                1,
                new Dictionary<string, object?>
                {
                    ["password_hash"] = new[] { "old-hash", "new-hash" },
                    ["name"] = new[] { "old", "new" }
                }),
            CancellationToken.None);

        var row = await context.AuditLogs.AsNoTracking().SingleAsync();
        using var diff = JsonDocument.Parse(row.Diff!);
        Assert.Equal("***", diff.RootElement.GetProperty("password_hash")[0].GetString());
        Assert.Equal("new", diff.RootElement.GetProperty("name")[1].GetString());
    }

    [Fact]
    public async Task SensitiveDiffValuesAreRedactedWhenInputIsJson()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        using var document = JsonDocument.Parse("""{"key_hash":["raw","hashed"]}""");

        await new AuditService().AppendAsync(
            context,
            new AuditEntry("update", "api_key", 1, document.RootElement),
            CancellationToken.None);

        var row = await context.AuditLogs.AsNoTracking().SingleAsync();
        using var diff = JsonDocument.Parse(row.Diff!);
        Assert.Equal("***", diff.RootElement.GetProperty("key_hash")[0].GetString());
    }

    [Fact]
    public async Task ConcurrentAppendsRemainVerifiable()
    {
        using var database = new TemporaryDatabase();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.EnsureCreatedAsync();
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            await using var context = database.CreateContext();
            await new AuditService().AppendAsync(
                context,
                new AuditEntry($"append-{index}", User: "worker"),
                CancellationToken.None);
        }));

        await using var verify = database.CreateContext();
        var result = await new AuditService().VerifyAsync(verify, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Equal(8, result.Checked);
    }

    [Fact]
    public async Task SealedAuditRowsCannotBeModifiedOrDeleted()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        var service = new AuditService();
        await service.AppendAsync(context, new AuditEntry("create"), CancellationToken.None);
        await service.SealAsync(context, CancellationToken.None);

        var row = await context.AuditLogs.SingleAsync();
        row.Kind = "changed";

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task AnyAuditRowCannotBeDeleted()
    {
        using var database = new TemporaryDatabase();
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        context.AuditLogs.Add(new AuditLogRow { Kind = "unsealed", Time = DateTime.Now });
        await context.SaveChangesAsync();
        context.AuditLogs.Remove(await context.AuditLogs.SingleAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"honeymom-audit-test-{Guid.NewGuid():N}");

        public TemporaryDatabase()
        {
            Directory.CreateDirectory(_directory);
        }

        public HoneyMomDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_directory, "audit.db")};Pooling=False;Default Timeout=10")
                .Options;
            return new HoneyMomDbContext(options);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
