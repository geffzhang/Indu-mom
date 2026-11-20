namespace HoneyMOM.Application.Entities;

public sealed record EntityQueryRequest(
    string Entity,
    int Page = 1,
    int Size = 20,
    string? Query = null,
    string? View = null,
    string? Sort = null,
    IReadOnlyDictionary<string, string?>? Filters = null);

public sealed record EntityQueryResult(
    long Total,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Items);

public sealed record EntityDetailResult(
    IReadOnlyDictionary<string, object?> Item,
    IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> Children);

public sealed record EntityOperationResult(
    bool Succeeded,
    long? Id = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
