namespace HoneyMOM.Application.Users;

public interface IUserAuthenticationStore
{
    Task<bool> NeedsAdminAsync(CancellationToken cancellationToken);

    Task<AuthenticationUser?> FindUserAsync(string username, CancellationToken cancellationToken);

    Task<AuthenticationUser?> CreateAdminAndAuditAsync(
        string username,
        string name,
        string passwordHash,
        CancellationToken cancellationToken);

    Task WriteLoginAuditAsync(
        string action,
        int? userId,
        string target,
        string username,
        CancellationToken cancellationToken);
}
