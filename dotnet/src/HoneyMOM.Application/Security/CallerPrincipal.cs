namespace HoneyMOM.Application.Security;

public sealed record CallerRole(
    string Code,
    IReadOnlyList<string> PermissionPatterns,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? Scope);

public sealed record CallerPrincipal(
    int? Id,
    string Username,
    string? Name,
    IReadOnlyList<CallerRole> Roles,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? ExplicitScope = null,
    IReadOnlyList<string>? AdditionalPermissionPatterns = null,
    bool IsApiKey = false,
    int? ApiKeyId = null,
    bool Active = true);
