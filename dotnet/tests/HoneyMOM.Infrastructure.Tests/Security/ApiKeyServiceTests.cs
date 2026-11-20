using System.Security.Cryptography;
using System.Text;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Security;

public sealed class ApiKeyServiceTests
{
    [Fact]
    public async Task CreateReturnsSecretOnlyOnceAndStoresHash()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);

        var created = await service.CreateAsync(
            new ApiKeyCreateRequest("  integration  ", ["operator"], ["system.audit.view"]),
            "admin",
            CancellationToken.None);

        Assert.Matches("^hmk_[0-9a-f]{8}_.+$", created.Key);
        var row = await db.ApiKeys.AsNoTracking().SingleAsync();
        var expectedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(created.Key))).ToLowerInvariant();
        Assert.Equal(expectedHash, row.KeyHash);
        Assert.DoesNotContain("key_hash", System.Text.Json.JsonSerializer.Serialize(created.Public));
        Assert.DoesNotContain(created.Key, System.Text.Json.JsonSerializer.Serialize(created.Public));
        Assert.Equal("integration", row.Name);
    }

    [Fact]
    public async Task KeyAuthenticationCombinesRoleAndExtraPermissions()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);
        var created = await service.CreateAsync(
            new ApiKeyCreateRequest("operator", ["operator"], ["system.audit.view"]),
            "admin",
            CancellationToken.None);

        var result = await service.AuthenticateAsync(created.Key, "127.0.0.1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Caller!.IsApiKey);
        Assert.True(PermissionEvaluator.Allows(result.Caller, "production.work_order.read"));
        Assert.True(PermissionEvaluator.Allows(result.Caller, "system.audit.view"));
        Assert.Equal(created.Public.Id, result.Caller.ApiKeyId);
    }

    [Fact]
    public async Task ExplicitKeyScopeIsApplied()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);
        var created = await service.CreateAsync(
            new ApiKeyCreateRequest(
                "scoped",
                ["admin"],
                Scope: new Dictionary<string, IReadOnlyList<int>> { ["plants"] = [5] }),
            "admin",
            CancellationToken.None);

        var result = await service.AuthenticateAsync(created.Key, "127.0.0.1", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal([5], ScopeEvaluator.ResolveEffectiveScope(result.Caller!)!.Plants);
    }

    [Fact]
    public async Task MalformedAndUnknownKeysReturnUnauthorizedFailures()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);

        var malformed = await service.AuthenticateAsync("not-a-key", null, CancellationToken.None);
        var unknown = await service.AuthenticateAsync("hmk_01234567_secret", null, CancellationToken.None);

        Assert.Equal(ApiKeyAuthenticationFailure.Malformed, malformed.Failure);
        Assert.Equal(ApiKeyAuthenticationFailure.Invalid, unknown.Failure);
    }

    [Fact]
    public async Task ExpiredAndRevokedKeysAreUnauthorized()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new ApiKeyService(db, clock);
        var expired = await service.CreateAsync(
            new ApiKeyCreateRequest("expired", ExpiresAt: clock.GetLocalNow().DateTime.AddSeconds(-1)),
            "admin",
            CancellationToken.None);
        var revoked = await service.CreateAsync(
            new ApiKeyCreateRequest("revoked"),
            "admin",
            CancellationToken.None);
        await service.RevokeAsync(revoked.Public.Id, "admin", CancellationToken.None);

        Assert.Equal(
            ApiKeyAuthenticationFailure.Expired,
            (await service.AuthenticateAsync(expired.Key, null, CancellationToken.None)).Failure);
        Assert.Equal(
            ApiKeyAuthenticationFailure.Revoked,
            (await service.AuthenticateAsync(revoked.Key, null, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task DisallowedIpReturnsForbiddenFailure()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);
        var created = await service.CreateAsync(
            new ApiKeyCreateRequest("allowlisted", IpAllow: "192.168.10.0/24"),
            "admin",
            CancellationToken.None);

        var result = await service.AuthenticateAsync(created.Key, "10.0.0.5", CancellationToken.None);

        Assert.Equal(ApiKeyAuthenticationFailure.IpNotAllowed, result.Failure);
    }

    [Fact]
    public async Task InvalidIpListIsRejected()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);

        await Assert.ThrowsAsync<ApiKeyValidationException>(() => service.CreateAsync(
            new ApiKeyCreateRequest("invalid", IpAllow: "not-an-ip"),
            "admin",
            CancellationToken.None));
    }

    [Fact]
    public async Task RevocationInvalidatesCacheImmediately()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var service = new ApiKeyService(db);
        var created = await service.CreateAsync(new ApiKeyCreateRequest("revoked"), "admin", CancellationToken.None);
        Assert.True((await service.AuthenticateAsync(created.Key, null, CancellationToken.None)).Succeeded);

        await service.RevokeAsync(created.Public.Id, "admin", CancellationToken.None);

        Assert.Equal(
            ApiKeyAuthenticationFailure.Revoked,
            (await service.AuthenticateAsync(created.Key, null, CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task CredentialCacheExpiresWithinTenSeconds()
    {
        using var database = new TemporaryDatabase();
        await using var db = database.CreateContext();
        await db.Database.EnsureCreatedAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new ApiKeyService(db, clock);
        var created = await service.CreateAsync(new ApiKeyCreateRequest("cached"), "admin", CancellationToken.None);
        Assert.True((await service.AuthenticateAsync(created.Key, null, CancellationToken.None)).Succeeded);

        await using (var otherContext = database.CreateContext())
        {
            var row = await otherContext.ApiKeys.SingleAsync();
            row.RevokedAt = clock.GetLocalNow().DateTime;
            await otherContext.SaveChangesAsync();
        }

        clock.Advance(TimeSpan.FromSeconds(11));
        var result = await service.AuthenticateAsync(created.Key, null, CancellationToken.None);

        Assert.Equal(ApiKeyAuthenticationFailure.Revoked, result.Failure);
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"honeymom-apikey-test-{Guid.NewGuid():N}");

        public TemporaryDatabase() => Directory.CreateDirectory(_directory);

        public HoneyMomDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_directory, "apikey.db")};Pooling=False;Default Timeout=10")
                .Options;
            return new HoneyMomDbContext(options);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan value) => _now += value;
    }
}
