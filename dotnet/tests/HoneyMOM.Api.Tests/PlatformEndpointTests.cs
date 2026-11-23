using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class PlatformEndpointTests
{
    [Fact]
    public async Task PlatformRoutesRequireAuthentication()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MeReturnsEffectivePermissionsScopeAndWritableModules()
    {
        using var host = new ApiTestHost();
        await PlatformTestSupport.InsertEntityAsync(
            host,
            "plant",
            new Dictionary<string, object?> { ["code"] = "P-1", ["name"] = "Foundry" });
        using var client = (await PlatformTestSupport.CreateClientAsync(
            host,
            scope: """{"plants":[1]}""")).Client;

        using var response = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal("admin", root.GetProperty("username").GetString());
        Assert.Contains("主数据", root.GetProperty("writable").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("*", root.GetProperty("perms")[0].EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, root.GetProperty("scope").GetProperty("plants")[0].GetInt32());
        Assert.Equal("Foundry", root.GetProperty("scope_label").GetString());
    }

    [Fact]
    public async Task MeMatchesReferenceSessionProjectionAndEnglishScopeLabel()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Add("X-Lang", "en");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.True(root.TryGetProperty("created_at", out _));
        Assert.True(root.TryGetProperty("updated_at", out _));
        Assert.False(root.TryGetProperty("is_api_key", out _));
        Assert.Equal("All", root.GetProperty("scope_label").GetString());
    }

    [Fact]
    public async Task PasswordChangeChecksOldPasswordAndMinimumLength()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var wrongOld = await client.PostAsJsonAsync(
            "/api/me/password",
            new { old = "wrong-password", @new = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongOld.StatusCode);
        using var wrongBody = JsonDocument.Parse(await wrongOld.Content.ReadAsStringAsync());
        using var wrongOldRequest = new HttpRequestMessage(HttpMethod.Post, "/api/me/password")
        {
            Content = JsonContent.Create(new { old = "wrong-password", @new = "short" })
        };
        wrongOldRequest.Headers.Add("X-Lang", "zh-CN");
        using var wrongOldChinese = await client.SendAsync(wrongOldRequest);
        using var wrongChineseBody = JsonDocument.Parse(await wrongOldChinese.Content.ReadAsStringAsync());
        using var shortPassword = await client.PostAsJsonAsync(
            "/api/me/password",
            new { old = "admin-password", @new = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        using var shortBody = JsonDocument.Parse(await shortPassword.Content.ReadAsStringAsync());
        using var valid = await client.PostAsJsonAsync(
            "/api/me/password",
            new { old = "admin-password", @new = "new-password" });

        Assert.Equal("Incorrect current password", wrongBody.RootElement.GetProperty("detail").GetString());
        Assert.Equal("原密码不对", wrongChineseBody.RootElement.GetProperty("detail").GetString());
        Assert.Equal("New password must be at least 6 characters", shortBody.RootElement.GetProperty("detail").GetString());
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
    }

    [Fact]
    public async Task PasswordChangeAuditsTheRedactedUserUpdate()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/me/password")
        {
            Content = JsonContent.Create(new { old = "admin-password", @new = "changed-password" })
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.8");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var audit = await db.AuditLogs.SingleAsync(row =>
            row.Action == "update" && row.Entity == "sys_user" && row.EntityId == 1);
        Assert.Equal("ui", audit.Source);
        Assert.Equal("203.0.113.8", audit.Ip);
        Assert.Contains("***", audit.Diff);
        Assert.DoesNotContain("changed-password", audit.Diff);
    }

    [Fact]
    public async Task EntityAuditUsesForwardedClientIp()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/e/plant")
        {
            Content = JsonContent.Create(new { code = "P-IP", name = "IP Foundry" })
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.9");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var audit = await db.AuditLogs.SingleAsync(row => row.Entity == "plant" && row.Action == "create");
        Assert.Equal("203.0.113.9", audit.Ip);
    }

    [Fact]
    public async Task MetaUsesSnapshotAndReturnsReferenceShape()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.GetAsync("/api/meta");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        var plant = root.GetProperty("entities").EnumerateArray()
            .Single(entity => entity.GetProperty("name").GetString() == "plant");

        Assert.Equal("master", plant.GetProperty("mkey").GetString());
        Assert.Contains(
            plant.GetProperty("fields").EnumerateArray(),
            field => field.GetProperty("name").GetString() == "code");
        Assert.True(root.GetProperty("roles").TryGetProperty("admin", out _));
        Assert.Equal("采购员", root.GetProperty("roles").GetProperty("buyer").GetString());
        Assert.Equal("en", root.GetProperty("lang").GetString());
        Assert.True(root.GetProperty("langs").GetArrayLength() >= 2);
        Assert.False(root.GetProperty("demo").GetBoolean());
    }

    [Fact]
    public async Task WhoamiReturnsSessionIdentityAndPermissionModes()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal("admin", root.GetProperty("username").GetString());
        Assert.False(root.GetProperty("is_api_key").GetBoolean());
        Assert.Contains("admin", root.GetProperty("roles").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("*", root.GetProperty("perms")[0].EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("scope").ValueKind);
    }

    [Fact]
    public async Task DefaultErpRoleIncludesItsReferencePermissionGrants()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(
            host,
            username: "buyer",
            roles: "buyer")).Client;

        using var response = await client.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var permissions = body.RootElement.GetProperty("perms")[0]
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();

        Assert.Contains("purchase.*", permissions);
        Assert.Contains("master.supplier.*", permissions);
        Assert.Contains("!cost.*", permissions);
    }
}
