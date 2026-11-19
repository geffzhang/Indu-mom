namespace HoneyMOM.Application.Metadata;

public sealed record EntityFieldSchema(
    string Name,
    string Label,
    string Type,
    bool Required,
    bool ReadOnly,
    bool IsList,
    bool Searchable,
    bool Unique,
    string? Reference,
    string? Auto,
    string? DefaultJson,
    string? EnumJson);

public sealed record EntityChildSchema(string Entity, string ForeignKey, string? ParentKey);

public sealed record EntityColumnSchema(
    string Name,
    string Type,
    int? Length,
    int? Precision,
    int? Scale,
    bool Nullable,
    bool PrimaryKey,
    bool AutoIncrement);

public sealed record EntityIndexSchema(string? Name, IReadOnlyList<string> Columns, bool Unique);

public sealed record EntityTableSchema(
    IReadOnlyList<EntityColumnSchema> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<EntityIndexSchema> Indexes);

public sealed record EntitySchema(
    string Name,
    string Label,
    string Module,
    bool Internal,
    bool Menu,
    string Order,
    IReadOnlyList<EntityFieldSchema> Fields,
    IReadOnlyList<EntityChildSchema> Children,
    IReadOnlyDictionary<string, string> Actions,
    EntityTableSchema Table);
