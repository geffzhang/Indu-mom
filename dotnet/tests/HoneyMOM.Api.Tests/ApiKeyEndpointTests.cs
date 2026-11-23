using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class ApiKeyEndpointTests
{
    [Fact]
    public async Task CreateDisclosesSecretOnceAndListOmitsSecretsAndHashes()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var created = await client.PostAsJsonAsync(
            "/api/admin/apikeys",
            new { name = "integration", roles = new[] { "operator" }, perms = new[] { "system.audit.view" } });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("key").GetString()!;
        Assert.StartsWith("hmk_", key, StringComparison.Ordinal);
        var keyId = createdBody.RootElement.GetProperty("id").GetInt32();

        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var audit = await db.AuditLogs.SingleAsync(row => row.Action == "apikey_create");
            using var detail = JsonDocument.Parse(audit.Detail!);
            Assert.Equal("integration", detail.RootElement.GetProperty("name").GetString());
            Assert.Equal("operator", detail.RootElement.GetProperty("roles")[0].GetString());
            Assert.Equal("system.audit.view", detail.RootElement.GetProperty("perms")[0].GetString());
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("scope").ValueKind);
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("expires_at").ValueKind);
            Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("ip_allow").ValueKind);
        }

        using var listed = await client.GetAsync("/api/admin/apikeys");
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var listJson = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("key_hash", listJson, StringComparison.Ordinal);
        using var listBody = JsonDocument.Parse(listJson);
        Assert.Equal(keyId, listBody.RootElement.GetProperty("items")[0].GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task ApiKeyAuthenticatesWhoamiAndCanBeRevoked()
    {
        using var host = new ApiTestHost();
        using var admin = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var created = await admin.PostAsJsonAsync(
            "/api/admin/apikeys",
            new { name = "operator", roles = new[] { "operator" }, perms = new[] { "system.audit.view" } });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("key").GetString()!;
        var keyId = createdBody.RootElement.GetProperty("id").GetInt32();

        using var keyClient = host.CreateClient();
        keyClient.DefaultRequestHeaders.Add("X-API-Key", key);
        using var whoami = await keyClient.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        using var whoamiBody = JsonDocument.Parse(await whoami.Content.ReadAsStringAsync());
        Assert.True(whoamiBody.RootElement.GetProperty("is_api_key").GetBoolean());
        Assert.Equal($"apikey:operator", whoamiBody.RootElement.GetProperty("username").GetString());
        Assert.Contains(
            whoamiBody.RootElement.GetProperty("perms").EnumerateArray()
                .SelectMany(group => group.EnumerateArray())
                .Select(permission => permission.GetString()),
            permission => permission == "system.audit.view");

        using var me = await keyClient.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var meBody = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.True(meBody.RootElement.GetProperty("is_api_key").GetBoolean());
        Assert.Equal("apikey:operator", meBody.RootElement.GetProperty("username").GetString());

        foreach (var scheme in new[] { "Bearer", "ApiKey" })
        {
            using var schemeClient = host.CreateClient();
            schemeClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, key);
            using var schemeResponse = await schemeClient.GetAsync("/api/whoami");
            Assert.Equal(HttpStatusCode.OK, schemeResponse.StatusCode);
        }

        using var revoked = await admin.PostAsync($"/api/admin/apikeys/{keyId}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        using var denied = await keyClient.GetAsync("/api/whoami");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task KeyUpdateChangesPublicFieldsWithoutReturningSecretOrHash()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var created = await client.PostAsJsonAsync(
            "/api/admin/apikeys",
            new { name = "before", roles = new[] { "operator" } });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("key").GetString()!;
        var keyId = createdBody.RootElement.GetProperty("id").GetInt32();

        using var updated = await client.PutAsJsonAsync(
            $"/api/admin/apikeys/{keyId}",
            new
            {
                name = "after",
                roles = new[] { "operator", "qc" },
                perms = new[] { "system.audit.view" },
                ip_allow = "10.0.0.0/8"
            });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var updateJson = await updated.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, updateJson, StringComparison.Ordinal);
        Assert.DoesNotContain("key_hash", updateJson, StringComparison.Ordinal);

        using var listed = await client.GetAsync("/api/admin/apikeys");
        using var listBody = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var record = listBody.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetInt32() == keyId);
        Assert.Equal("after", record.GetProperty("name").GetString());
        Assert.Equal("operator,qc", record.GetProperty("roles").GetString());
        Assert.Equal("10.0.0.0/8", record.GetProperty("ip_allow").GetString());
        Assert.Contains(
            record.GetProperty("perms").EnumerateArray().Select(permission => permission.GetString()),
            permission => permission == "system.audit.view");
    }

    [Fact]
    public async Task MalformedApiKeyReturnsUnauthorized()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "hmk_bad");

        using var response = await client.GetAsync("/api/whoami");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InvalidIpAllowlistReturnsBadRequest()
    {
        using var host = new ApiTestHost();
        using var client = (await PlatformTestSupport.CreateClientAsync(host)).Client;

        using var response = await client.PostAsJsonAsync(
            "/api/admin/apikeys",
            new { name = "invalid", ip_allow = "not-an-ip" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task DisallowedIpReturnsForbiddenWithReferenceError()
    {
        using var host = new ApiTestHost();
        using var admin = (await PlatformTestSupport.CreateClientAsync(host)).Client;
        using var created = await admin.PostAsJsonAsync(
            "/api/admin/apikeys",
            new { name = "allowlisted", ip_allow = "192.168.10.0/24" });
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var key = createdBody.RootElement.GetProperty("key").GetString()!;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/whoami");
        request.Headers.Add("X-API-Key", key);
        request.Headers.Add("X-Lang", "zh-CN");
        using var response = await host.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.EndsWith("调用（IP 白名单）", body.RootElement.GetProperty("detail").GetString());
    }
}
