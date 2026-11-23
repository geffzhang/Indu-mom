using HoneyMOM.Application.Auditing;
using HoneyMOM.Application.Users;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class EfUserAuthenticationStore(HoneyMomDbContext dbContext) : IUserAuthenticationStore
{
    public async Task<bool> NeedsAdminAsync(CancellationToken cancellationToken)
    {
        var activeRoles = await dbContext.SystemUsers
            .AsNoTracking()
            .Where(user => user.Active == true)
            .Select(user => user.Roles)
            .ToListAsync(cancellationToken);
        return !activeRoles.Any(HasAdministratorRole);
    }

    public async Task<AuthenticationUser?> FindUserAsync(
        string username,
        CancellationToken cancellationToken)
    {
        var user = await dbContext.SystemUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Username == username, cancellationToken);
        return user is null ? null : ToAuthenticationUser(user);
    }

    public async Task<AuthenticationUser?> CreateAdminAndAuditAsync(
        string username,
        string name,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var activeRoles = await dbContext.SystemUsers
            .AsNoTracking()
            .Where(user => user.Active == true)
            .Select(user => user.Roles)
            .ToListAsync(cancellationToken);
        if (activeRoles.Any(HasAdministratorRole))
        {
            return null;
        }

        var user = await dbContext.SystemUsers
            .SingleOrDefaultAsync(row => row.Username == username, cancellationToken);
        var now = DateTime.Now;
        if (user is null)
        {
            user = new SystemUserRow
            {
                Username = username,
                CreatedAt = now
            };
            dbContext.SystemUsers.Add(user);
        }

        user.Name = name;
        user.Roles = "admin";
        user.Active = true;
        user.PasswordHash = passwordHash;
        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);

        var request = AuditRequestContext.Current;
        dbContext.AuditLogs.Add(new AuditLogRow
        {
            Time = now,
            User = username[..Math.Min(username.Length, 80)],
            Kind = "create_admin",
            Target = "setup:admin",
            Entity = "sys_user",
            EntityId = user.Id,
            Action = "create_admin",
            Source = request?.Source ?? "ui",
            Ip = request?.Ip
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToAuthenticationUser(user);
    }

    public async Task WriteLoginAuditAsync(
        string action,
        int? userId,
        string target,
        string username,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var request = AuditRequestContext.Current;
        dbContext.AuditLogs.Add(new AuditLogRow
        {
            Time = DateTime.Now,
            User = username[..Math.Min(username.Length, 80)],
            Kind = action,
            Target = target[..Math.Min(target.Length, 190)],
            Entity = "sys_user",
            EntityId = userId,
            Action = action,
            Source = request?.Source ?? "ui",
            Ip = request?.Ip
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool HasAdministratorRole(string? roles) =>
        (roles ?? "").Split(',').Contains("admin", StringComparer.Ordinal);

    private static AuthenticationUser ToAuthenticationUser(SystemUserRow user) =>
        new(
            user.Id,
            user.Username ?? "",
            user.Name ?? "",
            user.Roles ?? "",
            user.PasswordHash,
            user.Active == true);
}
