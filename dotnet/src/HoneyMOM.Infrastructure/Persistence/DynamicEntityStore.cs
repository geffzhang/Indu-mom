using HoneyMOM.Application.Metadata;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class DynamicEntityStore
{
    private readonly HoneyMomDbContext _context;
    private readonly IEntitySchemaCatalog _catalog;
    private static readonly HashSet<string> TypedTables = new(StringComparer.Ordinal)
    {
        "sys_user",
        "sys_role",
        "audit_log",
        "setting"
    };

    public DynamicEntityStore(HoneyMomDbContext context, IEntitySchemaCatalog? catalog = null)
    {
        _context = context;
        _catalog = catalog ?? SchemaSnapshotCatalog.Default;
    }

    public async Task<long> InsertAsync(
        string entityName,
        IDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        var entity = GetGenericEntity(entityName);
        var row = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        _context.Set<Dictionary<string, object?>>(entity.Name).Add(row);
        await _context.SaveChangesAsync(cancellationToken);

        return Convert.ToInt64(row["id"], System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        string entityName,
        long id,
        CancellationToken cancellationToken)
    {
        var entity = GetGenericEntity(entityName);
        var row = await _context.Set<Dictionary<string, object?>>(entity.Name)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => EF.Property<int>(item, "id") == id,
                cancellationToken);
        return row;
    }

    private EntitySchema GetGenericEntity(string entityName)
    {
        var entity = _catalog.Get(entityName)
            ?? throw new KeyNotFoundException($"Unknown entity '{entityName}'.");
        if (TypedTables.Contains(entityName))
        {
            throw new NotSupportedException(
                $"Entity '{entityName}' uses a typed persistence adapter and cannot be accessed as a shared type.");
        }

        return entity;
    }
}
