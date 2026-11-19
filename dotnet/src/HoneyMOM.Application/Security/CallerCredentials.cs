namespace HoneyMOM.Application.Security;

public sealed record CallerCredentials(
    string? ApiKeyHeader,
    string? AuthorizationHeader,
    string? QueryToken,
    string? CookieToken,
    string? RemoteIp);
