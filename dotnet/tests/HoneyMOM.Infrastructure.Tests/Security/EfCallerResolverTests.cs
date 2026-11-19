using HoneyMOM.Application.Configuration;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Security;

public sealed class EfCallerResolverTests
{
    [Fact]
    public async Task ResolvesSessionTokenWithPersistedPermissionsAndScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var temporaryDirectory = new TemporaryDirectory();
        var dataDirectory = Path.Combine(temporaryDirectory.Path, "data");
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(dataDirectory, "secret.key"),
            new string('a', 64));
        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new HoneyMomDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.SystemUsers.Add(new SystemUserRow
        {
            Username = "planner",
            Name = "Planner",
            Roles = "planner",
            Active = true,
            Scope = """{"plants":[2]}"""
        });
        db.SystemRoles.Add(new SystemRoleRow
        {
            Code = "planner",
            Name = "Planner",
            Perms = """["production.*","!production.work_order.delete"]"""
        });
        await db.SaveChangesAsync();

        var tokenService = new PythonCompatibleTokenService(
            new MomOptions(dataDirectory, "", 30, 10));
        var token = tokenService.Create("planner", DateTimeOffset.UtcNow);
        var resolver = new EfCallerResolver(db, tokenService);
        var result = await resolver.ResolveAsync(
            new CallerCredentials(null, $"Bearer {token}", null, null, "127.0.0.1"),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(PermissionEvaluator.Allows(result.Caller!, "production.work_order.read"));
        Assert.False(PermissionEvaluator.Allows(result.Caller!, "production.work_order.delete"));
        Assert.Equal([2], ScopeEvaluator.ResolveEffectiveScope(result.Caller!)!.Plants);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"honeymom-caller-test-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
