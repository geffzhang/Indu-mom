using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class EntityEndpointTests
{
    [Fact]
    public async Task ListAppliesFiltersAndScope()
    {
        using var host = new ApiTestHost();
        var scopedId = checked((int)await InsertPlantAsync(host, "A-1", "Foundry scoped"));
        await InsertPlantAsync(host, "B-1", "Foundry outside");
        using var client = await CreateAdminClientAsync(host, [scopedId]);

        using var response = await client.GetAsync("/api/e/plant?q=Foundry");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("A-1", body.RootElement.GetProperty("items")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task SortAndPaginationMatchReference()
    {
        using var host = new ApiTestHost();
        await InsertPlantAsync(host, "C", "Charlie");
        await InsertPlantAsync(host, "A", "Alpha");
        await InsertPlantAsync(host, "B", "Bravo");
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.GetAsync("/api/e/plant?page=2&size=1&sort=name");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(3, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal("Bravo", body.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExportUsesUtf8BomHeadersAndQueryFilters()
    {
        using var host = new ApiTestHost();
        await InsertPlantAsync(host, "P-1", "Foundry");
        await InsertPlantAsync(host, "P-2", "Warehouse");
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.GetAsync("/api/e/plant.csv?q=Foundry");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var csv = Encoding.UTF8.GetString(bytes);

        Assert.True(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains("编码", csv, StringComparison.Ordinal);
        Assert.Contains("P-1", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("P-2", csv, StringComparison.Ordinal);
        Assert.Equal("attachment; filename=plant.csv", response.Content.Headers.ContentDisposition?.ToString());
    }

    [Fact]
    public async Task DetailReturnsLabelsAndChildren()
    {
        using var host = new ApiTestHost();
        var plantId = await InsertPlantAsync(host, "P-1", "Foundry");
        await InsertEntityAsync(host, "workshop", new Dictionary<string, object?>
        {
            ["code"] = "W-1",
            ["name"] = "Casting",
            ["plant"] = checked((int)plantId)
        });
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.GetAsync($"/api/e/plant/{plantId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("Foundry", body.RootElement.GetProperty("item").GetProperty("name").GetString());
        var workshop = body.RootElement.GetProperty("children").GetProperty("workshop")[0];
        Assert.Equal("P-1 Foundry", workshop.GetProperty("plant__label").GetString());
    }

    [Fact]
    public async Task CreateRejectsMissingRequiredField()
    {
        using var host = new ApiTestHost();
        using var client = await CreateAdminClientAsync(host);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/e/plant")
        {
            Content = JsonContent.Create(new { code = "P-1" })
        };
        request.Headers.Add("X-Lang", "zh-CN");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("请填写名称", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task CreateSetsCreationAndUpdateTimestamps()
    {
        using var host = new ApiTestHost();
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.PostAsJsonAsync("/api/e/plant", new { code = "P-1", name = "Foundry" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetInt64();
        using var detail = await client.GetAsync($"/api/e/plant/{id}");
        using var item = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var row = item.RootElement.GetProperty("item");

        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("created_at").ValueKind);
        Assert.Equal(
            row.GetProperty("created_at").GetString(),
            row.GetProperty("updated_at").GetString());
    }

    [Fact]
    public async Task CreateGeneratesAutoCodesFromReferencePattern()
    {
        using var host = new ApiTestHost();
        var productId = await InsertEntityAsync(host, "material", new Dictionary<string, object?>
        {
            ["code"] = "M-1",
            ["name"] = "Material"
        });
        using var client = await CreateAdminClientAsync(host);

        using var first = await client.PostAsJsonAsync("/api/e/work_order", new { product = productId, qty = 1 });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var second = await client.PostAsJsonAsync("/api/e/work_order", new { product = productId, qty = 2 });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var datePrefix = DateTime.Now.ToString("yyMMdd", System.Globalization.CultureInfo.InvariantCulture);

        using var firstDetail = await client.GetAsync(
            $"/api/e/work_order/{firstBody.RootElement.GetProperty("id").GetInt64()}");
        using var firstItem = JsonDocument.Parse(await firstDetail.Content.ReadAsStringAsync());
        using var secondDetail = await client.GetAsync(
            $"/api/e/work_order/{secondBody.RootElement.GetProperty("id").GetInt64()}");
        using var secondItem = JsonDocument.Parse(await secondDetail.Content.ReadAsStringAsync());

        Assert.Equal($"WO{datePrefix}001", firstItem.RootElement.GetProperty("item").GetProperty("code").GetString());
        Assert.Equal($"WO{datePrefix}002", secondItem.RootElement.GetProperty("item").GetProperty("code").GetString());
    }

    [Fact]
    public async Task NullAndEmptyValuesFollowReferenceCoercion()
    {
        using var host = new ApiTestHost();
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.PostAsJsonAsync(
            "/api/e/workshop",
            new { code = "W-1", name = "Casting", plant = "", wtype = "" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetInt64();
        using var read = await client.GetAsync($"/api/e/workshop/{id}");
        using var row = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("item").GetProperty("plant").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.RootElement.GetProperty("item").GetProperty("wtype").ValueKind);
    }

    [Fact]
    public async Task MalformedValueReturnsReferenceErrorBeforeWrite()
    {
        using var host = new ApiTestHost();
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.PostAsJsonAsync(
            "/api/e/workshop",
            new { code = "W-1", name = "Casting", plant = "not-a-number" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();

        Assert.Equal("工厂 格式不对：not-a-number", body.RootElement.GetProperty("detail").GetString());
        Assert.Equal(0, await db.Set<Dictionary<string, object?>>("workshop").CountAsync());
    }

    [Fact]
    public async Task UpdateIgnoresReadOnlyAndLabelFields()
    {
        using var host = new ApiTestHost();
        var id = await InsertPlantAsync(host, "P-1", "Before");
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.PutAsJsonAsync(
            $"/api/e/plant/{id}",
            new { name = "After", id = id + 50, created_at = "2000-01-01", code__label = "forged" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var read = await client.GetAsync($"/api/e/plant/{id}");
        using var body = JsonDocument.Parse(await read.Content.ReadAsStringAsync());

        Assert.Equal(id, body.RootElement.GetProperty("item").GetProperty("id").GetInt64());
        Assert.Equal("After", body.RootElement.GetProperty("item").GetProperty("name").GetString());
        Assert.NotEqual("2000-01-01", body.RootElement.GetProperty("item").GetProperty("created_at").GetString());
        Assert.Equal("P-1", body.RootElement.GetProperty("item").GetProperty("code").GetString());
    }

    [Fact]
    public async Task DuplicateConstraintReturnsReferenceError()
    {
        using var host = new ApiTestHost();
        await InsertPlantAsync(host, "P-1", "First");
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.PostAsJsonAsync("/api/e/plant", new { code = "P-1", name = "Second" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("编码重复", body.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnknownSortCannotBecomeSql()
    {
        using var host = new ApiTestHost();
        await InsertPlantAsync(host, "P-1", "Foundry");
        using var client = await CreateAdminClientAsync(host);

        using var response = await client.GetAsync("/api/e/plant?sort=name%20desc%3B%20DROP%20TABLE%20plant--");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(1, body.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task OutOfScopeReadIsNotFoundAndWriteIsForbidden()
    {
        using var host = new ApiTestHost();
        await InsertPlantAsync(host, "A-1", "Allowed");
        var hiddenId = await InsertPlantAsync(host, "B-1", "Hidden");
        using var client = await CreateAdminClientAsync(host, [1]);

        using var read = await client.GetAsync($"/api/e/plant/{hiddenId}");
        using var write = await client.PutAsJsonAsync($"/api/e/plant/{hiddenId}", new { name = "Changed" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task SystemUserResetPasswordIsDispatchedAndAudited()
    {
        using var host = new ApiTestHost();
        int targetId;
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var target = new SystemUserRow
            {
                Username = "target",
                Name = "Target",
                Roles = "operator",
                Active = true,
                PasswordHash = new PythonCompatiblePasswordHasher().Hash("old-password")
            };
            db.SystemUsers.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }

        using var client = await CreateAdminClientAsync(host);
        using var response = await client.PostAsJsonAsync(
            $"/api/e/sys_user/{targetId}/do/reset_pw",
            new { password = "new-password" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var verifyScope = host.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var savedUser = await verifyDb.SystemUsers.SingleAsync(user => user.Id == targetId);

        Assert.True(new PythonCompatiblePasswordHasher().Verify("new-password", savedUser.PasswordHash));
        var audit = await verifyDb.AuditLogs.AsNoTracking().SingleAsync(
            entry => entry.Action == "reset_pw" && entry.Entity == "sys_user" && entry.EntityId == targetId);
        Assert.Contains("password_hash", audit.Diff);
        using var detail = JsonDocument.Parse(audit.Detail!);
        Assert.Equal("Reset password", detail.RootElement.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("body").ValueKind);
        Assert.Equal(
            "Password reset",
            detail.RootElement.GetProperty("result").GetProperty("msg").GetString());
        Assert.DoesNotContain("new-password", audit.Detail);
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Password reset", body.RootElement.GetProperty("msg").GetString());
    }

    [Fact]
    public async Task GenericUpdateUsesTypedSystemUserAdapter()
    {
        using var host = new ApiTestHost();
        int targetId;
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var target = new SystemUserRow
            {
                Username = "target",
                Name = "Before",
                Roles = "operator",
                Active = true
            };
            db.SystemUsers.Add(target);
            await db.SaveChangesAsync();
            targetId = target.Id;
        }

        using var client = await CreateAdminClientAsync(host);
        using var response = await client.PutAsJsonAsync(
            $"/api/e/sys_user/{targetId}",
            new { name = "After" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var verifyScope = host.CreateScope();
        var saved = await verifyScope.ServiceProvider.GetRequiredService<HoneyMomDbContext>()
            .SystemUsers.AsNoTracking().SingleAsync(user => user.Id == targetId);
        Assert.Equal("After", saved.Name);
    }

    private static async Task<HttpClient> CreateAdminClientAsync(
        ApiTestHost host,
        IReadOnlyList<int>? plantScope = null)
    {
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            db.SystemUsers.Add(new SystemUserRow
            {
                Username = "admin",
                Name = "Admin",
                Roles = "admin",
                Active = true,
                Scope = plantScope is null ? null : JsonSerializer.Serialize(new { plants = plantScope }),
                PasswordHash = new PythonCompatiblePasswordHasher().Hash("admin-password")
            });
            await db.SaveChangesAsync();
        }

        var client = host.CreateClient();
        using var login = await client.PostAsJsonAsync(
            "/api/login",
            new { username = "admin", password = "admin-password" });
        login.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body.RootElement.GetProperty("token").GetString());
        return client;
    }

    private static async Task<long> InsertPlantAsync(ApiTestHost host, string code, string name) =>
        await InsertEntityAsync(host, "plant", new Dictionary<string, object?> { ["code"] = code, ["name"] = name });

    private static async Task<long> InsertEntityAsync(
        ApiTestHost host,
        string entity,
        Dictionary<string, object?> row)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        return await new DynamicEntityStore(db).InsertAsync(entity, row, CancellationToken.None);
    }
}
