namespace HoneyMOM.Domain.Users;

public sealed record SystemUser(
    int Id,
    string Username,
    string? Name,
    string Roles,
    bool Active,
    string PasswordHash,
    string? Scope);
