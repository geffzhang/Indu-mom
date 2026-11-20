using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class AdminAuditEndpointTests
{
    [Fact]
    public async Task AuditListRequiresViewPermissionAndSupportsFilters()
    {
        using var host = new ApiTestHost();
        await SeedAuditAsync(host);
        using var noPermission = (await PlatformTestSupport.CreateClientAsync(
            host,
            username: "operator",
            roles: "operator")).Client;
        using var denied = await noPermission.GetAsync("/api/admin/audit");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        await AddRoleAsync(host, "auditor", ["system.audit.view"]);
        using var auditor = (await PlatformTestSupport.CreateClientAsync(
            host,
            username: "auditor",
            roles: "auditor")).Client;
        using var response = await auditor.GetAsync(
            "/api/admin/audit?user=alice&entity=plant&action=create&page=1&size=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        var item = body.RootElement.GetProperty("items")[0];
        Assert.Equal("alice", item.GetProperty("user").GetString());
        Assert.Equal("plant", item.GetProperty("entity").GetString());
        Assert.Equal("create", item.GetProperty("action").GetString());
        Assert.True(item.GetProperty("seq").GetInt32() > 0);
    }

    [Fact]
    public async Task AuditFacetsReturnSortedDistinctValues()
    {
        using var host = new ApiTestHost();
        await SeedAuditAsync(host);
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.GetAsync("/api/admin/audit/facets");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var users = body.RootElement.GetProperty("users").EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Contains("alice", users);
        Assert.Equal(users.Order(StringComparer.Ordinal), users);
        Assert.Contains("ui", body.RootElement.GetProperty("sources").EnumerateObject()
            .Select(property => property.Name));
    }

    [Fact]
    public async Task AuditExportRequiresExportPermissionAndRecordsExport()
    {
        using var host = new ApiTestHost();
        await SeedAuditAsync(host);
        await AddRoleAsync(host, "auditor", ["system.audit.view"]);
        using var auditor = (await PlatformTestSupport.CreateClientAsync(
            host,
            username: "auditor",
            roles: "auditor")).Client;
        using var denied = await auditor.GetAsync("/api/admin/audit.csv");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        using var admin = (await PlatformTestSupport.CreateClientAsync(
            host,
            username: "admin",
            roles: "admin")).Client;
        using var response = await admin.GetAsync("/api/admin/audit.csv");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        Assert.Equal(1, await db.AuditLogs.CountAsync(row => row.Action == "audit_export"));
        using var verification = await admin.GetAsync("/api/admin/audit/verify");
        using var verificationBody = JsonDocument.Parse(await verification.Content.ReadAsStringAsync());
        Assert.True(verificationBody.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(verificationBody.RootElement.GetProperty("checked").GetInt32() >= 3);
    }

    [Fact]
    public async Task VerifyReturnsPythonShape()
    {
        using var host = new ApiTestHost();
        await SeedAuditAsync(host);
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.GetAsync("/api/admin/audit/verify");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("checked").GetInt32() >= 2);
        Assert.Equal(0, root.GetProperty("unsealed").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("problem").ValueKind);
    }

    [Fact]
    public async Task CsvUsesUtf8BomAndReferenceHeaders()
    {
        using var host = new ApiTestHost();
        await SeedAuditAsync(host);
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/audit.csv");
        request.Headers.Add("X-Lang", "zh-CN");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("\uFEFF", Encoding.UTF8.GetString(bytes));
        Assert.Contains("序号,时间,用户,动作,对象,对象ID,目标,来源,IP,改动,详情,哈希", Encoding.UTF8.GetString(bytes));
        Assert.Contains("\"plant:P-1,foundry\"", Encoding.UTF8.GetString(bytes));
    }

    private static async Task SeedAuditAsync(ApiTestHost host)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var audit = new AuditService();
        await audit.AppendAsync(
            db,
            new AuditEntry(
                "create",
                "plant",
                1,
                Detail: new { code = "P-1" },
                Target: "plant:P-1,foundry",
                User: "alice",
                Time: new DateTime(2024, 1, 2, 3, 4, 5)),
            CancellationToken.None);
        await audit.AppendAsync(
            db,
            new AuditEntry(
                "update",
                "line",
                2,
                Detail: new { code = "L-2" },
                Target: "line:L-2",
                User: "bob",
                Source: "api",
                Time: new DateTime(2024, 1, 3, 3, 4, 5)),
            CancellationToken.None);
    }

    private static async Task AddRoleAsync(
        ApiTestHost host,
        string code,
        IReadOnlyList<string> permissions)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        db.SystemRoles.Add(new SystemRoleRow
        {
            Code = code,
            Name = code,
            Perms = JsonSerializer.Serialize(permissions)
        });
        await db.SaveChangesAsync();
    }
}
