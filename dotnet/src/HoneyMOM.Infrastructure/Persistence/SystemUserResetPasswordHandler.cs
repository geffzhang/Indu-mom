using System.Text.Json;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Entities;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class SystemUserResetPasswordHandler(
    HoneyMomDbContext context,
    IPasswordHasher passwordHasher) : IEntityActionHandler
{
    public string EntityName => "sys_user";
    public string ActionName => "reset_pw";

    public async Task<IReadOnlyDictionary<string, object?>> ExecuteAsync(
        EntityActionContext action,
        CancellationToken cancellationToken)
    {
        var password = action.Body.TryGetValue("password", out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new EntityOperationException(400, "新密码至少 8 位");
        }

        var user = await context.SystemUsers.SingleOrDefaultAsync(
            row => row.Id == action.EntityId,
            cancellationToken);
        if (user is null)
        {
            throw new EntityOperationException(404, "记录不存在");
        }

        user.PasswordHash = passwordHasher.Hash(password);
        user.UpdatedAt = DateTime.Now;
        await context.SaveChangesAsync(cancellationToken);
        return new Dictionary<string, object?> { ["msg"] = "密码已重置" };
    }
}
