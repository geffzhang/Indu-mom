using System.Reflection;
using System.Text.Json;
using HoneyMOM.Application.Metadata;

namespace HoneyMOM.Infrastructure.Persistence.Metadata;

public sealed class SchemaSnapshotCatalog : IEntitySchemaCatalog
{
    private const int SupportedVersion = 1;
    private const string ResourceSuffix = ".Persistence.Metadata.schema-v1.json";
    private static readonly Lazy<SchemaSnapshotCatalog> DefaultCatalog = new(() => new SchemaSnapshotCatalog());
    private readonly IReadOnlyDictionary<string, EntitySchema> _byName;

    public SchemaSnapshotCatalog()
    {
        using var stream = OpenSnapshot();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetInt32();
        if (version != SupportedVersion)
        {
            throw new InvalidDataException(
                $"Unsupported entity schema snapshot version {version}; supported version is {SupportedVersion}.");
        }

        var tables = root.GetProperty("tables");
        var entities = root.GetProperty("entities")
            .EnumerateArray()
            .Select(entity => ParseEntity(entity, tables))
            .ToArray();
        var duplicate = entities
            .GroupBy(entity => entity.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Duplicate entity in schema snapshot: {duplicate.Key}.");
        }

        Entities = Array.AsReadOnly(entities);
        RawEntities = Array.AsReadOnly(root.GetProperty("entities")
            .EnumerateArray()
            .Select(entity => entity.Clone())
            .ToArray());
        _byName = entities.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
    }

    public static SchemaSnapshotCatalog Default => DefaultCatalog.Value;

    public IReadOnlyList<EntitySchema> Entities { get; }

    public IReadOnlyList<JsonElement> RawEntities { get; }

    public EntitySchema? Get(string entityName) =>
        _byName.TryGetValue(entityName, out var entity) ? entity : null;

    private static Stream OpenSnapshot()
    {
        var assembly = typeof(SchemaSnapshotCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        if (resource is null)
        {
            throw new InvalidOperationException(
                $"Embedded schema snapshot resource ending in '{ResourceSuffix}' was not found.");
        }

        return assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Could not open embedded schema snapshot '{resource}'.");
    }

    private static EntitySchema ParseEntity(JsonElement element, JsonElement tables)
    {
        var name = RequiredString(element, "name");
        if (!tables.TryGetProperty(name, out var tableElement))
        {
            throw new InvalidDataException($"Schema snapshot has no physical table metadata for entity '{name}'.");
        }

        var fields = element.GetProperty("fields")
            .EnumerateArray()
            .Select(field => new EntityFieldSchema(
                RequiredString(field, "name"),
                OptionalString(field, "label") ?? RequiredString(field, "name"),
                OptionalString(field, "t") ?? "str",
                OptionalBoolean(field, "req"),
                OptionalBoolean(field, "ro"),
                OptionalBoolean(field, "list", defaultValue: true),
                OptionalBoolean(field, "search"),
                OptionalBoolean(field, "unique"),
                OptionalString(field, "ref"),
                OptionalString(field, "auto"),
                RawJson(field, "default"),
                RawJson(field, "enum")))
            .ToArray();

        var children = element.GetProperty("children")
            .EnumerateArray()
            .Select(child =>
            {
                var parts = child.EnumerateArray().ToArray();
                if (parts.Length is < 2 or > 3)
                {
                    throw new InvalidDataException($"Invalid child metadata for entity '{name}'.");
                }

                return new EntityChildSchema(
                    parts[0].GetString() ?? throw new InvalidDataException("Child entity name is null."),
                    parts[1].GetString() ?? throw new InvalidDataException("Child foreign-key name is null."),
                    parts.Length == 3 ? parts[2].GetString() : null);
            })
            .ToArray();

        var actions = element.GetProperty("actions")
            .EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => property.Value.GetString() ?? property.Name,
                StringComparer.Ordinal);

        var table = new EntityTableSchema(
            tableElement.GetProperty("columns")
                .EnumerateArray()
                .Select(column => ParseColumn(column))
                .ToArray(),
            tableElement.GetProperty("primary_key")
                .EnumerateArray()
                .Select(value => value.GetString() ?? throw new InvalidDataException("Primary key name is null."))
                .ToArray(),
            tableElement.GetProperty("indexes")
                .EnumerateArray()
                .Select(index => new EntityIndexSchema(
                    OptionalString(index, "name"),
                    index.GetProperty("columns").EnumerateArray()
                        .Select(value => value.GetString() ?? throw new InvalidDataException("Index column name is null."))
                        .ToArray(),
                    OptionalBoolean(index, "unique")))
                .ToArray());

        return new EntitySchema(
            name,
            OptionalString(element, "label") ?? name,
            OptionalString(element, "module") ?? "",
            OptionalBoolean(element, "internal"),
            OptionalBoolean(element, "menu", defaultValue: true),
            OptionalString(element, "order") ?? "-id",
            fields,
            children,
            actions,
            table);
    }

    private static EntityColumnSchema ParseColumn(JsonElement column)
    {
        var type = column.GetProperty("type");
        return new EntityColumnSchema(
            RequiredString(column, "name"),
            RequiredString(type, "name"),
            OptionalInt32(type, "length"),
            OptionalInt32(type, "precision"),
            OptionalInt32(type, "scale"),
            OptionalBoolean(column, "nullable"),
            OptionalBoolean(column, "primary_key"),
            OptionalBoolean(column, "autoincrement"));
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"Schema snapshot property '{property}' is null.");

    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool OptionalBoolean(JsonElement element, string property, bool defaultValue = false) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static int? OptionalInt32(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? RawJson(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetRawText() : null;
}
