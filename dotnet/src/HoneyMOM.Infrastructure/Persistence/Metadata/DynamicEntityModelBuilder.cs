using HoneyMOM.Application.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace HoneyMOM.Infrastructure.Persistence.Metadata;

public static class DynamicEntityModelBuilder
{
    private static readonly HashSet<string> TypedTables = new(StringComparer.Ordinal)
    {
        "sys_user",
        "sys_role",
        "audit_log",
        "setting"
    };

    public static void Apply(ModelBuilder modelBuilder, IEntitySchemaCatalog catalog)
    {
        foreach (var schema in catalog.Entities)
        {
            if (TypedTables.Contains(schema.Name))
            {
                continue;
            }

            modelBuilder.SharedTypeEntity<Dictionary<string, object?>>(
                schema.Name,
                builder => ConfigureEntity(builder, schema));
        }
    }

    private static void ConfigureEntity(
        EntityTypeBuilder<Dictionary<string, object?>> entity,
        EntitySchema schema)
    {
        entity.ToTable(schema.Name);
        foreach (var column in schema.Table.Columns)
        {
            var clrType = ClrType(column.Type);
            if (column.Nullable && clrType.IsValueType)
            {
                clrType = Nullable.GetUnderlyingType(clrType) ?? clrType;
                clrType = typeof(Nullable<>).MakeGenericType(clrType);
            }

            var property = entity.IndexerProperty(clrType, column.Name);
            property.IsRequired(!column.Nullable);
            if (column.Type == "String" && column.Length is > 0)
            {
                property.HasMaxLength(column.Length.Value);
            }
            else if (column.Type == "JSON")
            {
                property.HasColumnType("json");
            }
            else if (column.Type == "Date")
            {
                property.HasColumnType("date");
            }

            if (column.PrimaryKey && column.AutoIncrement is true)
            {
                property.ValueGeneratedOnAdd();
            }
        }

        if (schema.Table.PrimaryKey.Count == 0)
        {
            throw new InvalidDataException($"Entity '{schema.Name}' has no primary key in the schema snapshot.");
        }

        entity.HasKey(schema.Table.PrimaryKey.ToArray());
        foreach (var index in schema.Table.Indexes)
        {
            if (index.Columns.Count == 0)
            {
                continue;
            }

            var indexBuilder = entity.HasIndex(index.Columns.ToArray()).IsUnique(index.Unique);
            if (!string.IsNullOrWhiteSpace(index.Name))
            {
                indexBuilder.HasDatabaseName(index.Name);
            }
        }
    }

    private static Type ClrType(string type) => type switch
    {
        "String" or "Text" or "JSON" => typeof(string),
        "Integer" => typeof(int),
        "BigInteger" => typeof(long),
        "SmallInteger" => typeof(short),
        "Float" => typeof(double),
        "Boolean" => typeof(bool),
        "Date" => typeof(DateOnly),
        "DateTime" => typeof(DateTime),
        _ => throw new InvalidDataException($"Unsupported SQLAlchemy column type '{type}' in schema snapshot.")
    };
}
