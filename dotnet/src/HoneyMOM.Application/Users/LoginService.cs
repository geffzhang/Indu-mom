using System.Text;
using HoneyMOM.Application.Abstractions;

namespace HoneyMOM.Application.Users;

public sealed class LoginService(
    IUserAuthenticationStore store,
    IPasswordHasher passwordHasher,
    ITokenService tokenService)
{
    public async Task<AuthenticationOperationResult> LoginAsync(
        string? username,
        string? password,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = (username ?? "").Trim();
        var user = await store.FindUserAsync(normalizedUsername, cancellationToken);
        if (user is null || !user.Active || !passwordHasher.Verify(password ?? "", user.PasswordHash))
        {
            await store.WriteLoginAuditAsync(
                "login_fail",
                user?.Id,
                $"login:{normalizedUsername[..Math.Min(normalizedUsername.Length, 60)]}",
                normalizedUsername.Length == 0
                    ? "?"
                    : normalizedUsername[..Math.Min(normalizedUsername.Length, 80)],
                cancellationToken);
            return new AuthenticationOperationResult(null, "账号或密码不对");
        }

        await store.WriteLoginAuditAsync(
            "login",
            user.Id,
            $"login:{normalizedUsername}",
            normalizedUsername,
            cancellationToken);
        return new AuthenticationOperationResult(
            SetupAdminService.ToResponse(user, tokenService.Create(user.Username, now)),
            null);
    }
}
