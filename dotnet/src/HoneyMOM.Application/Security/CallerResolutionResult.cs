namespace HoneyMOM.Application.Security;

public enum CallerResolutionFailure
{
    None,
    Unauthenticated,
    InactiveUser,
    InvalidApiKey,
    MalformedApiKey,
    ExpiredApiKey,
    RevokedApiKey,
    ApiKeyNotAllowed,
    ApiKeyIpNotAllowed
}

public sealed record CallerResolutionResult(
    CallerPrincipal? Caller,
    CallerResolutionFailure Failure)
{
    public bool Succeeded => Caller is not null && Failure == CallerResolutionFailure.None;

    public static CallerResolutionResult Success(CallerPrincipal caller) =>
        new(caller, CallerResolutionFailure.None);

    public static CallerResolutionResult Failed(CallerResolutionFailure failure) =>
        new(null, failure);
}
