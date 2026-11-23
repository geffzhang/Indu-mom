using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class AdminIdentityEndpointTests
{
    [Fact]
    public async Task PermissionCatalogIncludesEntityMetadataAndRegisteredActions()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.GetAsync("/api/admin/perms/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = body.RootElement.GetProperty("entities").EnumerateArray()
            .Single(entity => entity.GetProperty("entity").GetString() == "sys_user");

        Assert.False(user.GetProperty("internal").GetBoolean());
        Assert.Contains(
            user.GetProperty("perms").EnumerateArray(),
            permission => permission.GetProperty("id").GetString() == "system.sys_user.reset_pw");
    }

    [Fact]
    public async Task RoleMutationRequiresManagePermission()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(
            host,
            username: "operator",
            roles: "operator")).Client;

        using var response = await client.PostAsJsonAsync(
            "/api/admin/roles",
            new { code = "auditor", name = "Auditor", perms = new[] { "system.audit.view" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleCreationNormalizesFieldsAndWritesAuditRecord()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.PostAsJsonAsync(
            "/api/admin/roles",
            new
            {
                code = " Auditor ",
                name = "  Auditor  ",
                perms = new[] { " system.audit.view ", "system.audit.view", "not valid" },
                scope = new { plants = new object[] { "2", 1, 2 }, workshops = Array.Empty<int>(), lines = Array.Empty<int>() },
                remark = "review"
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var role = await db.SystemRoles.SingleAsync(row => row.Code == "auditor");
            Assert.Equal("Auditor", role.Name);
            Assert.Equal("[\"system.audit.view\"]", role.Perms);
            Assert.Equal("""{"plants":[1,2],"workshops":[],"lines":[]}""", role.Scope);
            Assert.Equal("review", role.Remark);
            Assert.False(role.Builtin);
            Assert.Equal(1, await db.AuditLogs.CountAsync(row => row.Target == "role:auditor"));
        }
    }

    [Fact]
    public async Task BuiltinRoleCannotBeDeleted()
    {
        using var host = new ApiTestHost();
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            db.SystemRoles.Add(new SystemRoleRow
            {
                Code = "admin",
                Name = "System Admin",
                Perms = "[\"*\"]",
                Builtin = true
            });
            await db.SaveChangesAsync();
        }

        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var response = await client.DeleteAsync("/api/admin/roles/admin");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "Built-in roles cannot be deleted (you can change their permissions)",
            body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task AdminCannotRemoveOwnAdminRole()
    {
        using var host = new ApiTestHost();
        var (client, userId) = await PlatformTestSupport.CreateClientAsync(host);

        using var response = await client.PutAsJsonAsync(
            $"/api/admin/users/{userId}",
            new { roles = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "You cannot remove your own administrator role",
            body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UserScopeUpdateWritesAnAuditRecordWithRequestIp()
    {
        using var host = new ApiTestHost();
        var (client, userId) = await PlatformTestSupport.CreateClientAsync(host);
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/admin/users/{userId}")
        {
            Content = JsonContent.Create(new
            {
                roles = new[] { "admin" },
                scope = new { plants = new[] { 1 }, workshops = Array.Empty<int>(), lines = Array.Empty<int>() }
            })
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var audit = await db.AuditLogs.SingleAsync(row =>
            row.Action == "update" && row.Entity == "sys_user" && row.EntityId == userId);
        Assert.Equal("admin", audit.User);
        Assert.Equal("ui", audit.Source);
        Assert.Equal("203.0.113.10", audit.Ip);
        Assert.Contains("scope", audit.Diff);
    }

    [Fact]
    public async Task OrganizationTreeIncludesOrphans()
    {
        using var host = new ApiTestHost();
        var plantId = await PlatformTestSupport.InsertEntityAsync(
            host,
            "plant",
            new Dictionary<string, object?> { ["code"] = "P-1", ["name"] = "Foundry" });
        await PlatformTestSupport.InsertEntityAsync(
            host,
            "workshop",
            new Dictionary<string, object?> { ["code"] = "W-1", ["name"] = "Casting", ["plant"] = (int)plantId });
        await PlatformTestSupport.InsertEntityAsync(
            host,
            "line",
            new Dictionary<string, object?> { ["code"] = "L-1", ["name"] = "Molding", ["workshop"] = 1 });
        await PlatformTestSupport.InsertEntityAsync(
            host,
            "workshop",
            new Dictionary<string, object?> { ["code"] = "W-2", ["name"] = "Orphan workshop" });
        await PlatformTestSupport.InsertEntityAsync(
            host,
            "line",
            new Dictionary<string, object?> { ["code"] = "L-2", ["name"] = "Orphan line" });
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.GetAsync("/api/admin/org");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal("Foundry", root.GetProperty("plants")[0].GetProperty("name").GetString());
        Assert.Equal("Molding", root.GetProperty("plants")[0].GetProperty("workshops")[0]
            .GetProperty("lines")[0].GetProperty("name").GetString());
        Assert.Equal("Orphan workshop", root.GetProperty("orphans").GetProperty("workshops")[0]
            .GetProperty("name").GetString());
        Assert.Equal("Orphan line", root.GetProperty("orphans").GetProperty("lines")[0]
            .GetProperty("name").GetString());
    }
}
