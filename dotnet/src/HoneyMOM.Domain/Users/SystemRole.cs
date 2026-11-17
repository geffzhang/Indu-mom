namespace HoneyMOM.Domain.Users;

public sealed record SystemRole(
    int Id,
    string Code,
    string? Name,
    string? Permissions,
    string? Scope,
    bool Builtin,
    string? Remark);
