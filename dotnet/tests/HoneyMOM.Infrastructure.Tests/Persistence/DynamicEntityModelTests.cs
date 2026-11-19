using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Persistence;

public sealed class DynamicEntityModelTests
{
    [Fact]
    public void SnapshotCatalogLoadsEveryEntity()
    {
        var catalog = new SchemaSnapshotCatalog();

        Assert.NotNull(catalog.Get("plant"));
        Assert.NotNull(catalog.Get("work_order"));
        Assert.NotNull(catalog.Get("ncr"));
        Assert.Null(catalog.Get("not_an_entity"));
        Assert.Equal(146, catalog.Entities.Count);
    }

    [Fact]
    public void DynamicModelMapsGenericTablesWithoutDuplicatingTypedTables()
    {
        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var context = new HoneyMomDbContext(options);

        Assert.NotNull(context.Model.FindEntityType("plant"));
        Assert.NotNull(context.Model.FindEntityType("work_order"));
        Assert.Single(context.Model.GetEntityTypes(), entity => entity.GetTableName() == "sys_user");
        Assert.Single(context.Model.GetEntityTypes(), entity => entity.GetTableName() == "sys_role");
        Assert.Single(context.Model.GetEntityTypes(), entity => entity.GetTableName() == "audit_log");
        Assert.Single(context.Model.GetEntityTypes(), entity => entity.GetTableName() == "setting");
    }

    [Fact]
    public async Task DynamicStoreWritesAndReadsSharedTypeRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new HoneyMomDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var store = new DynamicEntityStore(context);

        var id = await store.InsertAsync(
            "plant",
            new Dictionary<string, object?> { ["code"] = "DYN-1", ["name"] = "Dynamic Plant" },
            CancellationToken.None);
        var row = await store.GetAsync("plant", id, CancellationToken.None);

        Assert.Equal(id, Convert.ToInt64(row?["id"], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("DYN-1", row?["code"]);
        Assert.Equal("Dynamic Plant", row?["name"]);
    }
}
