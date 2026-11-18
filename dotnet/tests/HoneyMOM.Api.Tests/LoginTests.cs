using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HoneyMOM.Api.Tests;

public sealed class LoginTests
{
    [Fact]
    public async Task InvalidCredentialsReturnGeneric401()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var hasher = new PythonCompatiblePasswordHasher();
            db.SystemUsers.AddRange(
                new SystemUserRow
                {
                    Username = "active-user",
                    Name = "Active User",
                    Roles = "operator",
                    Active = true,
                    PasswordHash = hasher.Hash("correct-password")
                },
                new SystemUserRow
                {
                    Username = "inactive-user",
                    Name = "Inactive User",
                    Roles = "operator",
                    Active = false,
                    PasswordHash = hasher.Hash("correct-password")
                });
            await db.SaveChangesAsync();
        }

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Post, "/api/login?lang=en")
        {
            Content = JsonContent.Create(new { username = "missing-user", password = "wrong-password" })
        };
        invalidRequest.Headers.Add("X-Lang", "zh-CN");
        invalidRequest.Headers.Add("Accept-Language", "en");
        using var invalid = await client.SendAsync(invalidRequest);

        using var inactiveRequest = new HttpRequestMessage(HttpMethod.Post, "/api/login")
        {
            Content = JsonContent.Create(new { username = "inactive-user", password = "correct-password" })
        };
        inactiveRequest.Headers.Add("X-Lang", "zh-CN");
        using var inactive = await client.SendAsync(inactiveRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, inactive.StatusCode);
        Assert.Equal(
            "账号或密码不对",
            await ReadDetailAsync(invalid));
        Assert.Equal(await ReadDetailAsync(invalid), await ReadDetailAsync(inactive));
        Assert.Equal("zh-CN", invalid.Content.Headers.ContentLanguage.Single());
        Assert.Equal("zh-CN", inactive.Content.Headers.ContentLanguage.Single());

        using var scopeAfter = host.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        Assert.Equal(2, await dbAfter.AuditLogs.CountAsync(log => log.Action == "login_fail"));
    }

    [Fact]
    public async Task ActiveUserCanLoginAndAuditUsesResolvedLanguage()
    {
        using var host = new ApiTestHost();
        using var client = host.CreateClient();
        using (var scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            db.SystemUsers.Add(new SystemUserRow
            {
                Username = "operator",
                Name = "Operator",
                Roles = "operator,planner",
                Active = true,
                PasswordHash = new PythonCompatiblePasswordHasher().Hash("correct-password")
            });
            await db.SaveChangesAsync();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/login?lang=en")
        {
            Content = JsonContent.Create(new { username = " operator ", password = "correct-password" })
        };
        request.Headers.Add("Accept-Language", "zh-CN");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("en", response.Content.Headers.ContentLanguage.Single());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var user = body.RootElement.GetProperty("user");
        Assert.Equal("operator", user.GetProperty("username").GetString());
        Assert.Equal("Operator", user.GetProperty("name").GetString());
        Assert.Equal("operator,planner", user.GetProperty("roles").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("token").GetString()));

        using var scopeAfter = host.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        Assert.Equal(1, await dbAfter.AuditLogs.CountAsync(log => log.Action == "login"));
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("detail").GetString();
    }
}
