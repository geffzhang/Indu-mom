using System.Globalization;
using System.Text.Json;
using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Api.Endpoints;

public static class AdminApiKeyEndpoints
{
    private const string ManagePermission = "system.apikey.manage";

    public static IEndpointRouteBuilder MapAdminApiKeyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/admin/apikeys",
            async (
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                ApiKeyService apiKeys,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, ManagePermission))
                {
                    return Forbidden(context);
                }

                var records = await apiKeys.ListAsync(cancellationToken);
                var items = new List<object>(records.Count);
                foreach (var record in records)
                {
                    items.Add(await ToResponseAsync(
                        db,
                        record,
                        cancellationToken,
                        RequestLanguageMiddleware.GetLanguage(context)));
                }

                var storedRoles = await db.SystemRoles.AsNoTracking()
                    .OrderBy(role => role.Id)
                    .Select(role => new { code = role.Code, name = role.Name })
                    .ToListAsync(cancellationToken);
                var language = RequestLanguageMiddleware.GetLanguage(context);
                var roles = storedRoles.Count > 0
                    ? storedRoles
                        .Select(role => new
                        {
                            code = role.code ?? "",
                            name = RequestLanguageMiddleware.Translate(role.name ?? "", language)
                        })
                        .ToList()
                    : EfCallerResolver.DefaultRoles
                        .Select(role => new
                        {
                            code = role.Key,
                            name = RequestLanguageMiddleware.Translate(role.Value.Name, language)
                        })
                        .ToList();
                return Results.Json(new { items, roles });
            });

        endpoints.MapPost(
            "/api/admin/apikeys",
            async (
                Dictionary<string, JsonElement> body,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                ApiKeyService apiKeys,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, ManagePermission))
                {
                    return Forbidden(context);
                }

                ApiKeyCreated created;
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    created = await apiKeys.CreateAsync(ToCreateRequest(body), caller!.Username, cancellationToken);
                }
                catch (ApiKeyValidationException exception)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, exception.Message);
                }

                await audit.AppendAsync(
                    db,
                    new AuditEntry(
                        "apikey_create",
                        "api_key",
                        created.Public.Id,
                        Detail: CreateAuditDetail(body, includeName: true, includeDefaults: true),
                        Target: $"apikey:{created.Public.Id}",
                        User: caller!.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new
                {
                    ok = true,
                    id = created.Public.Id,
                    key = created.Key,
                    msg = RequestLanguageMiddleware.Translate(
                        "密钥只显示这一次，请立即复制保存",
                        RequestLanguageMiddleware.GetLanguage(context))
                });
            });

        endpoints.MapPut(
            "/api/admin/apikeys/{kid:int}",
            async (
                int kid,
                Dictionary<string, JsonElement> body,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                ApiKeyService apiKeys,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, ManagePermission))
                {
                    return Forbidden(context);
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    if (!await apiKeys.UpdateAsync(kid, body, cancellationToken))
                    {
                        return EntityEndpoints.Error(context, StatusCodes.Status404NotFound, "没有这个密钥");
                    }
                }
                catch (ApiKeyValidationException exception)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, exception.Message);
                }

                await audit.AppendAsync(
                    db,
                    new AuditEntry(
                        "apikey_update",
                        "api_key",
                        kid,
                        Detail: CreateAuditDetail(body, includeName: true),
                        Target: $"apikey:{kid}",
                        User: caller!.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new { ok = true });
            });

        endpoints.MapPost(
            "/api/admin/apikeys/{kid:int}/revoke",
            async (
                int kid,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                ApiKeyService apiKeys,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, ManagePermission))
                {
                    return Forbidden(context);
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await apiKeys.RevokeAsync(kid, caller!.Username, cancellationToken);
                await audit.AppendAsync(
                    db,
                    new AuditEntry(
                        "apikey_revoke",
                        "api_key",
                        kid,
                        Target: $"apikey:{kid}",
                        User: caller.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new { ok = true });
            });

        return endpoints;
    }

    private static async Task<object> ToResponseAsync(
        HoneyMomDbContext db,
        ApiKeyPublicRecord record,
        CancellationToken cancellationToken,
        string language)
    {
        var scope = ToScopeSelection(record.Scope);
        return new
        {
            id = record.Id,
            name = record.Name,
            key_hint = record.KeyHint,
            roles = record.Roles,
            perms = record.Permissions,
            scope = PlatformEndpoints.ScopeResponse(scope),
            scope_label = await PlatformEndpoints.ScopeLabelAsync(db, scope, cancellationToken, language),
            ip_allow = record.IpAllow,
            expires_at = record.ExpiresAt,
            revoked_at = record.RevokedAt,
            revoked_by = record.RevokedBy,
            last_used_at = record.LastUsedAt,
            last_used_ip = record.LastUsedIp,
            use_count = record.UseCount,
            remark = record.Remark,
            created_by = record.CreatedBy,
            created_at = record.CreatedAt,
            status = record.Status
        };
    }

    private static ApiKeyCreateRequest ToCreateRequest(IReadOnlyDictionary<string, JsonElement> body)
    {
        var expiresAt = GetDateTime(body, "expires_at");
        return new ApiKeyCreateRequest(
            GetString(body, "name") ?? "",
            GetStringListOrDelimited(body, "roles"),
            GetStringList(body, "perms"),
            GetScope(body, "scope"),
            expiresAt,
            GetString(body, "ip_allow"),
            GetString(body, "remark"));
    }

    private static object CreateAuditDetail(
        IReadOnlyDictionary<string, JsonElement> body,
        bool includeName = false,
        bool includeDefaults = false)
    {
        var keys = new[] { "roles", "perms", "scope", "expires_at", "ip_allow" }
            .Concat(includeName ? ["name"] : [])
            .Where(key => includeDefaults || body.ContainsKey(key))
            .ToDictionary(
                key => key,
                key => body.TryGetValue(key, out var value) ? (object?)value : null,
                StringComparer.Ordinal);
        return keys;
    }

    private static IReadOnlyList<string> GetStringList(
        IReadOnlyDictionary<string, JsonElement> body,
        string key) =>
        body.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static IReadOnlyList<string> GetStringListOrDelimited(
        IReadOnlyDictionary<string, JsonElement> body,
        string key)
    {
        if (!body.TryGetValue(key, out var value))
        {
            return [];
        }

        return value.ValueKind == JsonValueKind.Array
            ? GetStringList(body, key)
            : (value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>>? GetScope(
        IReadOnlyDictionary<string, JsonElement> body,
        string key)
    {
        if (!body.TryGetValue(key, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scope = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var name in new[] { "plants", "workshops", "lines" })
        {
            var ids = new SortedSet<int>();
            if (value.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in values.EnumerateArray())
                {
                    if (int.TryParse(item.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    {
                        ids.Add(id);
                    }
                }
            }

            scope[name] = ids.ToArray();
        }

        return scope.Values.Any(ids => ids.Count > 0) ? scope : null;
    }

    private static DateTime? GetDateTime(IReadOnlyDictionary<string, JsonElement> body, string key)
    {
        if (!body.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
        {
            return null;
        }

        return DateTime.TryParse(
            value.ToString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var result)
                ? result
                : throw new ApiKeyValidationException("日期时间格式不对");
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> body, string key) =>
        body.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static ScopeSelection? ToScopeSelection(
        IReadOnlyDictionary<string, IReadOnlyList<int>>? scope) =>
        scope is null
            ? null
            : new ScopeSelection(
                scope.GetValueOrDefault("plants") ?? [],
                scope.GetValueOrDefault("workshops") ?? [],
                scope.GetValueOrDefault("lines") ?? []);

    private static IResult Forbidden(HttpContext context) =>
        EntityEndpoints.Error(context, StatusCodes.Status403Forbidden, $"没有权限：{ManagePermission}");
}
