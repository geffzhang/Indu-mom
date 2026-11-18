using System.Text;
using HoneyMOM.Application.Abstractions;

namespace HoneyMOM.Application.Users;

public sealed class SetupAdminService(
    IUserAuthenticationStore store,
    IPasswordHasher passwordHasher,
    ITokenService tokenService)
{
    private static readonly SemaphoreSlim SetupLock = new(1, 1);

    public Task<bool> NeedsAdminAsync(CancellationToken cancellationToken) =>
        store.NeedsAdminAsync(cancellationToken);

    public async Task<AuthenticationOperationResult> CreateAsync(
        string? username,
        string? password,
        string? name,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await SetupLock.WaitAsync(cancellationToken);
        try
        {
            if (!await store.NeedsAdminAsync(cancellationToken))
            {
                return new AuthenticationOperationResult(null, "管理员账号已存在");
            }

            var normalizedUsername = (username ?? "").Trim();
            if (normalizedUsername.Length == 0 || normalizedUsername.EnumerateRunes().Count() > 40)
            {
                return new AuthenticationOperationResult(null, "请填写账号");
            }

            if ((password ?? "").EnumerateRunes().Count() < 8)
            {
                return new AuthenticationOperationResult(null, "新密码至少 8 位");
            }

            var user = await store.CreateAdminAndAuditAsync(
                normalizedUsername,
                string.IsNullOrEmpty(name) ? normalizedUsername : name,
                passwordHasher.Hash(password!),
                cancellationToken);
            if (user is null)
            {
                return new AuthenticationOperationResult(null, "管理员账号已存在");
            }

            var response = ToResponse(user, tokenService.Create(user.Username, now));
            return new AuthenticationOperationResult(response, null);
        }
        finally
        {
            SetupLock.Release();
        }
    }

    internal static AuthenticationResponse ToResponse(AuthenticationUser user, string token) =>
        new(token, new AuthenticationResponseUser(user.Username, user.Name, user.Roles));
}
