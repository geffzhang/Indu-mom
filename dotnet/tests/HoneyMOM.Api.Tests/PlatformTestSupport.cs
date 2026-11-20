using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace HoneyMOM.Api.Tests;

internal static class PlatformTestSupport
{
    public static async Task<(HttpClient Client, int UserId)> CreateClientAsync(
        ApiTestHost host,
        string username = "admin",
        string roles = "admin",
        string password = "admin-password",
        string? scope = null)
    {
        int userId;
        using (var serviceScope = host.CreateScope())
        {
            var db = serviceScope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
            var user = new SystemUserRow
            {
                Username = username,
                Name = username,
                Roles = roles,
                Active = true,
                Scope = scope,
                PasswordHash = new PythonCompatiblePasswordHasher().Hash(password)
            };
            db.SystemUsers.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = host.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/login", new { username, password });
        login.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            body.RootElement.GetProperty("token").GetString());
        return (client, userId);
    }

    public static async Task<long> InsertEntityAsync(
        ApiTestHost host,
        string entity,
        Dictionary<string, object?> values)
    {
        using var scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        return await new DynamicEntityStore(db).InsertAsync(entity, values, CancellationToken.None);
    }
}
