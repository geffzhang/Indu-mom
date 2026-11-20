namespace HoneyMOM.Application.Security;

public sealed record ApiKeyCreateRequest(
    string Name,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Permissions = null,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? Scope = null,
    DateTime? ExpiresAt = null,
    string? IpAllow = null,
    string? Remark = null);

public sealed record ApiKeyPublicRecord(
    int Id,
    string Name,
    string KeyHint,
    string Roles,
    IReadOnlyList<string> Permissions,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? Scope,
    string IpAllow,
    DateTime? ExpiresAt,
    DateTime? RevokedAt,
    string? RevokedBy,
    DateTime? LastUsedAt,
    string? LastUsedIp,
    int UseCount,
    string Remark,
    string? CreatedBy,
    DateTime? CreatedAt,
    string Status);

public sealed record ApiKeyCreated(ApiKeyPublicRecord Public, string Key);

public enum ApiKeyAuthenticationFailure
{
    None,
    Malformed,
    Invalid,
    Expired,
    Revoked,
    IpNotAllowed
}

public sealed record ApiKeyAuthenticationResult(
    CallerPrincipal? Caller,
    ApiKeyAuthenticationFailure Failure)
{
    public bool Succeeded => Caller is not null && Failure == ApiKeyAuthenticationFailure.None;

    public static ApiKeyAuthenticationResult Success(CallerPrincipal caller) =>
        new(caller, ApiKeyAuthenticationFailure.None);

    public static ApiKeyAuthenticationResult Failed(ApiKeyAuthenticationFailure failure) =>
        new(null, failure);
}
