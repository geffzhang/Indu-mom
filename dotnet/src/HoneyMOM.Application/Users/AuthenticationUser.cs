namespace HoneyMOM.Application.Users;

public sealed record AuthenticationUser(
    int Id,
    string Username,
    string Name,
    string Roles,
    string? PasswordHash,
    bool Active);
