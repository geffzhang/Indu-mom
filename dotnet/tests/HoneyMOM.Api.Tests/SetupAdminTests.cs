using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class SetupAdminTests
{
    [Fact]
    public async Task ConcurrentFirstAdminRequestsCreateAtMostOneAdministrator()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();

        using var setupResponse = await client.GetAsync("/api/setup/admin");
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        Assert.Equal("en", setupResponse.Content.Headers.ContentLanguage.Single());
        using var setupState = JsonDocument.Parse(await setupResponse.Content.ReadAsStringAsync());
        Assert.True(setupState.RootElement.GetProperty("needed").GetBoolean());
        Assert.False(setupState.RootElement.GetProperty("demo").GetBoolean());

        var requests = Enumerable.Range(0, 2)
            .Select(_ => client.PostAsJsonAsync(
                "/api/setup/admin",
                new { username = "  first-admin  ", password = "password-123", name = "" }))
            .ToArray();
        using var first = await requests[0];
        using var second = await requests[1];
        var responses = new[] { first, second };
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);

        var success = responses.Single(response => response.StatusCode == HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        var user = body.RootElement.GetProperty("user");
        Assert.Equal("first-admin", user.GetProperty("username").GetString());
        Assert.Equal("first-admin", user.GetProperty("name").GetString());
        Assert.Equal("admin", user.GetProperty("roles").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("token").GetString()));

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        Assert.Equal(1, await db.SystemUsers.CountAsync(user => user.Roles == "admin" && user.Active == true));
        Assert.Equal(1, await db.AuditLogs.CountAsync(log => log.Action == "create_admin"));
        Assert.False(await SetupNeededAsync(client));
    }

    [Fact]
    public async Task SetupValidationAndInactiveUserReactivationMatchPythonBehavior()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();

        using var shortPassword = await client.PostAsJsonAsync(
            "/api/setup/admin",
            new { username = "admin", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.Equal(
            "New password must be at least 8 characters",
            await GetDetailAsync(shortPassword));

        using var longUsername = await client.PostAsJsonAsync(
            "/api/setup/admin",
            new { username = new string('u', 41), password = "password-123" });
        Assert.Equal(HttpStatusCode.BadRequest, longUsername.StatusCode);
        Assert.Equal("Please enter a username", await GetDetailAsync(longUsername));

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            db.SystemUsers.Add(new HoneyMOM.Infrastructure.Persistence.Entities.SystemUserRow
            {
                Username = "returning-user",
                Name = "Old name",
                Roles = "operator",
                Active = false,
                PasswordHash = "old-hash"
            });
            await db.SaveChangesAsync();
        }

        using var reactivated = await client.PostAsJsonAsync(
            "/api/setup/admin",
            new { username = "returning-user", password = "password-123", name = "" });
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var user = await db.SystemUsers.SingleAsync(row => row.Username == "returning-user");
            Assert.True(user.Active);
            Assert.Equal("admin", user.Roles);
            Assert.Equal("returning-user", user.Name);
        }

        using var rejected = await client.PostAsJsonAsync(
            "/api/setup/admin",
            new { username = "another-user", password = "password-123" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal("An administrator account already exists", await GetDetailAsync(rejected));
    }

    [Fact]
    public async Task SetupUserAndAuditWritesRollbackTogether()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TRIGGER reject_admin_audit BEFORE INSERT ON audit_log
                BEGIN
                    SELECT RAISE(ABORT, 'audit insert rejected');
                END
                """);
        }

        using var response = await client.PostAsJsonAsync(
            "/api/setup/admin",
            new { username = "rolled-back-admin", password = "password-123" });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        using var verifyScope = host.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        Assert.False(await verifyDb.SystemUsers.AnyAsync(user => user.Username == "rolled-back-admin"));
        Assert.Empty(await verifyDb.AuditLogs.ToListAsync());
    }

    private static async Task<bool> SetupNeededAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/setup/admin");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("needed").GetBoolean();
    }

    private static async Task<string?> GetDetailAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("detail").GetString();
    }
}
