namespace HoneyMOM.Application.Security;

public interface ICallerResolver
{
    Task<CallerResolutionResult> ResolveAsync(
        CallerCredentials credentials,
        CancellationToken cancellationToken);
}
