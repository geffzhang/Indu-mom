using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Security;

public sealed class ApiKeyService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UsageUpdateInterval = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, CacheEntry> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<int, DateTimeOffset> LastUsageUpdates = new();

    private readonly HoneyMomDbContext _db;
    private readonly TimeProvider _timeProvider;

    public ApiKeyService(HoneyMomDbContext db, TimeProvider? timeProvider = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ApiKeyCreated> CreateAsync(
        ApiKeyCreateRequest request,
        string createdBy,
        CancellationToken cancellationToken)
    {
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new ApiKeyValidationException("请填写名称");
        }

        var ipAllow = (request.IpAllow ?? "").Trim();
        ValidateIpAllowlist(ipAllow);

        string prefix;
        do
        {
            prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        }
        while (await _db.ApiKeys.AnyAsync(row => row.Prefix == prefix, cancellationToken));

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(30))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var key = $"hmk_{prefix}_{secret}";
        var now = TruncateToSecond(_timeProvider.GetLocalNow().DateTime);
        var normalizedScope = NormalizeScope(request.Scope);
        var row = new ApiKeyRow
        {
            Name = name[..Math.Min(name.Length, 100)],
            Prefix = prefix,
            KeyHash = HashKey(key),
            Roles = NormalizeRoles(request.Roles),
            Perms = JsonSerializer.Serialize(NormalizePermissions(request.Permissions)),
            Scope = SerializeScope(normalizedScope),
            IpAllow = ipAllow,
            ExpiresAt = request.ExpiresAt,
            Remark = (request.Remark ?? "")[..Math.Min((request.Remark ?? "").Length, 300)],
            CreatedBy = createdBy[..Math.Min(createdBy.Length, 80)],
            CreatedAt = now,
            UseCount = 0
        };
        _db.ApiKeys.Add(row);
        await _db.SaveChangesAsync(cancellationToken);
        return new ApiKeyCreated(ToPublic(row), key);
    }

    public async Task<ApiKeyAuthenticationResult> AuthenticateAsync(
        string key,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        if (!TryGetPrefix(key, out var prefix))
        {
            return ApiKeyAuthenticationResult.Failed(ApiKeyAuthenticationFailure.Malformed);
        }

        var row = await GetCachedRowAsync(prefix, cancellationToken);
        if (row is null || !FixedTimeHashMatches(row.KeyHash, key))
        {
            return ApiKeyAuthenticationResult.Failed(ApiKeyAuthenticationFailure.Invalid);
        }

        var now = _timeProvider.GetLocalNow().DateTime;
        if (row.RevokedAt is not null)
        {
            return ApiKeyAuthenticationResult.Failed(ApiKeyAuthenticationFailure.Revoked);
        }

        if (row.ExpiresAt is DateTime expiresAt && expiresAt < now)
        {
            return ApiKeyAuthenticationResult.Failed(ApiKeyAuthenticationFailure.Expired);
        }

        if (!IsIpAllowed(row.IpAllow, remoteIp))
        {
            return ApiKeyAuthenticationResult.Failed(ApiKeyAuthenticationFailure.IpNotAllowed);
        }

        var roles = await ResolveRolesAsync(row.Roles, cancellationToken);
        await UpdateLastUseAsync(row.Id, remoteIp, cancellationToken);
        return ApiKeyAuthenticationResult.Success(new CallerPrincipal(
            Id: null,
            Username: $"apikey:{row.Name}",
            Name: $"API key {row.Name}",
            Roles: roles,
            ExplicitScope: ParseScope(row.Scope),
            AdditionalPermissionPatterns: ParseStringArray(row.Perms),
            IsApiKey: true,
            ApiKeyId: row.Id));
    }

    public async Task<IReadOnlyList<ApiKeyPublicRecord>> ListAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetLocalNow().DateTime;
        var rows = await _db.ApiKeys.AsNoTracking()
            .OrderByDescending(row => row.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(row => ToPublic(row, now)).ToArray();
    }

    public async Task<ApiKeyPublicRecord?> GetAsync(int id, CancellationToken cancellationToken)
    {
        var row = await _db.ApiKeys.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return row is null ? null : ToPublic(row, _timeProvider.GetLocalNow().DateTime);
    }

    public async Task<bool> UpdateAsync(
        int id,
        IReadOnlyDictionary<string, JsonElement> values,
        CancellationToken cancellationToken)
    {
        var row = await _db.ApiKeys.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
        {
            return false;
        }

        if (values.TryGetValue("name", out var name))
        {
            row.Name = ReadString(name).Trim()[..Math.Min(ReadString(name).Trim().Length, 100)];
        }

        if (values.TryGetValue("roles", out var roles))
        {
            row.Roles = NormalizeRoles(ReadStringListOrDelimited(roles));
        }

        if (values.TryGetValue("remark", out var remark))
        {
            var value = ReadString(remark).Trim();
            row.Remark = value[..Math.Min(value.Length, 300)];
        }

        if (values.TryGetValue("ip_allow", out var ipAllow))
        {
            row.IpAllow = ReadString(ipAllow).Trim();
            ValidateIpAllowlist(row.IpAllow);
        }

        if (values.TryGetValue("perms", out var permissions))
        {
            row.Perms = JsonSerializer.Serialize(NormalizePermissions(ReadStringList(permissions)));
        }

        if (values.TryGetValue("scope", out var scope))
        {
            row.Scope = SerializeScope(NormalizeScope(scope));
        }

        if (values.TryGetValue("expires_at", out var expiresAt))
        {
            row.ExpiresAt = ParseDateTime(expiresAt);
        }

        await _db.SaveChangesAsync(cancellationToken);
        Invalidate(row.Prefix);
        return true;
    }

    public async Task RevokeAsync(
        int id,
        string revokedBy,
        CancellationToken cancellationToken)
    {
        var row = await _db.ApiKeys.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is not null)
        {
            row.RevokedAt = TruncateToSecond(_timeProvider.GetLocalNow().DateTime);
            row.RevokedBy = revokedBy[..Math.Min(revokedBy.Length, 80)];
            await _db.SaveChangesAsync(cancellationToken);
            Invalidate(row.Prefix);
            LastUsageUpdates.TryRemove(id, out _);
        }
    }

    public static void ValidateIpAllowlist(string value)
    {
        foreach (var item in SplitIpAllowlist(value))
        {
            if (!IPNetwork.TryParse(item, out _))
            {
                throw new ApiKeyValidationException(
                    $"IP 白名单格式不对：{item}（写 IP 或网段，如 10.0.0.5、192.168.1.0/24）");
            }
        }
    }

    private async Task<ApiKeyRow?> GetCachedRowAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (Cache.TryGetValue(prefix, out var cached) && now - cached.CachedAt < CacheDuration)
        {
            return Clone(cached.Row);
        }

        var row = await _db.ApiKeys.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Prefix == prefix, cancellationToken);
        Cache[prefix] = new CacheEntry(Clone(row), now);
        return row;
    }

    private async Task<IReadOnlyList<CallerRole>> ResolveRolesAsync(
        string? rolesValue,
        CancellationToken cancellationToken)
    {
        var roleCodes = (rolesValue ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = await _db.SystemRoles.AsNoTracking()
            .Where(role => roleCodes.Contains(role.Code!))
            .ToListAsync(cancellationToken);
        return rows.Count == 0
            ? roleCodes
                .Where(EfCallerResolver.DefaultRoles.ContainsKey)
                .Select(code => new CallerRole(
                    code,
                    EfCallerResolver.DefaultRoles[code].Permissions,
                    null))
                .ToArray()
            : rows.Select(row => new CallerRole(
                row.Code ?? "",
                ParseStringArray(row.Perms),
                ParseScope(row.Scope)))
                .ToArray();
    }

    private async Task UpdateLastUseAsync(
        int id,
        string? remoteIp,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        while (true)
        {
            if (LastUsageUpdates.TryGetValue(id, out var previous) &&
                now - previous < UsageUpdateInterval)
            {
                return;
            }

            if (LastUsageUpdates.TryAdd(id, now) || LastUsageUpdates.TryUpdate(id, now, previous))
            {
                break;
            }
        }

        await _db.ApiKeys
            .Where(row => row.Id == id)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(row => row.LastUsedAt, TruncateToSecond(_timeProvider.GetLocalNow().DateTime))
                    .SetProperty(row => row.LastUsedIp, remoteIp)
                    .SetProperty(row => row.UseCount, row => (row.UseCount ?? 0) + 1),
                cancellationToken);
    }

    private ApiKeyPublicRecord ToPublic(ApiKeyRow row) =>
        ToPublic(row, _timeProvider.GetLocalNow().DateTime);

    private static ApiKeyPublicRecord ToPublic(ApiKeyRow row, DateTime now)
    {
        var scope = ParseScope(row.Scope);
        var status = row.RevokedAt is not null
            ? "revoked"
            : row.ExpiresAt is DateTime expiresAt && expiresAt < now
                ? "expired"
                : "active";
        return new ApiKeyPublicRecord(
            row.Id,
            row.Name ?? "",
            $"hmk_{row.Prefix}_…",
            row.Roles ?? "",
            ParseStringArray(row.Perms),
            scope,
            row.IpAllow ?? "",
            row.ExpiresAt,
            row.RevokedAt,
            row.RevokedBy,
            row.LastUsedAt,
            row.LastUsedIp,
            row.UseCount ?? 0,
            row.Remark ?? "",
            row.CreatedBy,
            row.CreatedAt,
            status);
    }

    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static bool FixedTimeHashMatches(string? storedHash, string key)
    {
        var expected = Encoding.ASCII.GetBytes(storedHash ?? "");
        var actual = Encoding.ASCII.GetBytes(HashKey(key));
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static bool TryGetPrefix(string key, out string prefix)
    {
        prefix = "";
        if (!key.StartsWith("hmk_", StringComparison.Ordinal))
        {
            return false;
        }

        var separator = key.IndexOf('_', "hmk_".Length);
        if (separator <= "hmk_".Length || separator == key.Length - 1)
        {
            return false;
        }

        prefix = key["hmk_".Length..separator];
        return true;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>>? NormalizeScope(
        IReadOnlyDictionary<string, IReadOnlyList<int>>? scope)
    {
        if (scope is null)
        {
            return null;
        }

        var normalized = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
        {
            ["plants"] = NormalizeIds(scope, "plants"),
            ["workshops"] = NormalizeIds(scope, "workshops"),
            ["lines"] = NormalizeIds(scope, "lines")
        };
        return normalized.Values.Any(ids => ids.Count > 0) ? normalized : null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>>? NormalizeScope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scope = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var key in new[] { "plants", "workshops", "lines" })
        {
            var ids = new SortedSet<int>();
            if (value.TryGetProperty(key, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (int.TryParse(item.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        ids.Add(id);
                    }
                }
            }

            scope[key] = ids.ToArray();
        }

        return scope.Values.Any(ids => ids.Count > 0) ? scope : null;
    }

    private static IReadOnlyList<int> NormalizeIds(
        IReadOnlyDictionary<string, IReadOnlyList<int>> scope,
        string key) =>
        scope.TryGetValue(key, out var ids) ? ids.Distinct().Order().ToArray() : [];

    private static string? SerializeScope(IReadOnlyDictionary<string, IReadOnlyList<int>>? scope) =>
        scope is null ? null : JsonSerializer.Serialize(scope);

    private static IReadOnlyDictionary<string, IReadOnlyList<int>>? ParseScope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return NormalizeScope(document.RootElement);
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return ReadStringList(document.RootElement);
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static IReadOnlyList<string> NormalizePermissions(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeRoles(IEnumerable<string>? values) =>
        string.Join(',', (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()));

    private static IReadOnlyList<string> ReadStringListOrDelimited(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? ReadStringList(value)
            : ReadString(value)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ReadString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static DateTime? ParseDateTime(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }

        return DateTime.TryParse(
            value.ToString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var parsed)
                ? parsed
                : throw new ApiKeyValidationException("日期时间格式不对");
    }

    private static bool IsIpAllowed(string? allow, string? ip)
    {
        var networks = new List<IPNetwork>();
        foreach (var item in SplitIpAllowlist(allow ?? ""))
        {
            if (!IPNetwork.TryParse(item, out var network))
            {
                return false;
            }

            networks.Add(network);
        }

        if (networks.Count == 0)
        {
            return true;
        }

        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        return networks.Any(network => network is IPNetwork parsed && parsed.Contains(address));
    }

    private static IEnumerable<string> SplitIpAllowlist(string value) =>
        value.Replace(',', ' ').Replace(';', ' ')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static ApiKeyRow? Clone(ApiKeyRow? row) =>
        row is null ? null : new ApiKeyRow
        {
            Id = row.Id,
            Name = row.Name,
            Prefix = row.Prefix,
            KeyHash = row.KeyHash,
            Roles = row.Roles,
            Perms = row.Perms,
            Scope = row.Scope,
            IpAllow = row.IpAllow,
            ExpiresAt = row.ExpiresAt,
            RevokedAt = row.RevokedAt,
            RevokedBy = row.RevokedBy,
            LastUsedAt = row.LastUsedAt,
            LastUsedIp = row.LastUsedIp,
            UseCount = row.UseCount,
            Remark = row.Remark,
            CreatedBy = row.CreatedBy,
            CreatedAt = row.CreatedAt
        };

    private static void Invalidate(string? prefix)
    {
        if (!string.IsNullOrEmpty(prefix))
        {
            Cache.TryRemove(prefix, out _);
        }
    }

    private static DateTime TruncateToSecond(DateTime value) =>
        value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond));

    private sealed record CacheEntry(ApiKeyRow? Row, DateTimeOffset CachedAt);
}

public sealed class ApiKeyValidationException(string message) : Exception(message);
