using System.Data.Common;
using System.Text.Json;
using Dapper;
using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using HoneyMOM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Api.Endpoints;

public static class PlatformEndpoints
{
    internal static readonly IReadOnlyDictionary<string, string> ModuleKeys =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["主数据"] = "master",
            ["生产管理"] = "production",
            ["质量管理"] = "quality",
            ["设备管理"] = "equipment",
            ["仓储物流"] = "warehouse",
            ["能源管理"] = "energy",
            ["铸造"] = "foundry",
            ["机加"] = "machining",
            ["AI 助手"] = "ai",
            ["系统"] = "system",
            ["实施"] = "impl",
            ["工厂模型"] = "factory",
            ["标签打印"] = "label",
            ["包装发运"] = "pack",
            ["委外加工"] = "outsource",
            ["备件管理"] = "spare",
            ["工艺变更"] = "ecn",
            ["文件受控"] = "doc",
            ["量检具"] = "gauge",
            ["IATF 质量"] = "iatf",
            ["客诉召回"] = "complaint",
            ["销售管理"] = "sales",
            ["采购管理"] = "purchase",
            ["计划 MRP"] = "mrp",
            ["成本核算"] = "cost",
            ["财务"] = "finance",
            ["ERP 集成"] = "erp"
        };

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/me",
            async (
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                IEntitySchemaCatalog catalog,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var user = caller!.IsApiKey
                    ? null
                    : await db.SystemUsers.AsNoTracking()
                        .SingleOrDefaultAsync(row => row.Id == caller.Id, cancellationToken);
                if (!caller.IsApiKey && user is null)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status401Unauthorized, "账号已停用");
                }

                var scope = ScopeResponse(ScopeEvaluator.ResolveEffectiveScope(caller!));
                var scopeLabel = await ScopeLabelAsync(
                    db,
                    ScopeEvaluator.ResolveEffectiveScope(caller!),
                    cancellationToken,
                    RequestLanguageMiddleware.GetLanguage(context));
                var writable = catalog.Entities
                    .Where(entity => new[] { "create", "update", "delete" }
                        .Any(operation => PermissionEvaluator.Allows(
                            caller!,
                            $"{ModuleKeys.GetValueOrDefault(entity.Module, entity.Module)}.{entity.Name}.{operation}")))
                    .Select(entity => entity.Module)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var userInfo = new Dictionary<string, object?>
                {
                    ["id"] = user?.Id,
                    ["username"] = caller.Username,
                    ["name"] = caller.Name,
                    ["roles"] = caller.IsApiKey
                        ? string.Join(',', caller.Roles.Select(role => role.Code))
                        : user!.Roles,
                    ["role_list"] = caller.Roles.Select(role => role.Code).ToArray(),
                    ["employee"] = user?.Employee,
                    ["active"] = user?.Active ?? true,
                    ["scope"] = scope,
                    ["company"] = await db.Settings.AsNoTracking()
                        .Where(setting => setting.Key == "company")
                        .Select(setting => setting.Value)
                        .FirstOrDefaultAsync(cancellationToken) ?? "",
                    ["now"] = DateTime.Now,
                    ["writable"] = writable,
                    ["perms"] = PermissionPatterns(caller)
                };
                if (caller.IsApiKey)
                {
                    userInfo["is_api_key"] = true;
                }

                if (user is not null)
                {
                    userInfo["created_at"] = user.CreatedAt;
                    userInfo["updated_at"] = user.UpdatedAt;
                }

                userInfo["scope_label"] = scopeLabel;
                return Results.Json(userInfo);
            });

        endpoints.MapPost(
            "/api/me/password",
            async (
                Dictionary<string, JsonElement> body,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                IPasswordHasher passwordHasher,
                AuditService auditService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var user = await db.SystemUsers.SingleOrDefaultAsync(
                    row => row.Id == caller!.Id,
                    cancellationToken);
                if (user is null)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status401Unauthorized, "账号已停用");
                }

                var oldPassword = GetString(body, "old") ?? "";
                if (!passwordHasher.Verify(oldPassword, user.PasswordHash))
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "原密码不对");
                }

                var newPassword = GetString(body, "new") ?? "";
                if (newPassword.Length < 6)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "新密码至少 6 位");
                }

                var previousHash = user.PasswordHash;
                user.PasswordHash = passwordHasher.Hash(newPassword);
                user.UpdatedAt = DateTime.Now;
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await auditService.AppendAsync(
                    db,
                    new AuditEntry(
                        "update",
                        "sys_user",
                        user.Id,
                        Diff: new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["password_hash"] = new[] { previousHash, user.PasswordHash }
                        },
                        User: caller!.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new { ok = true });
            });

        endpoints.MapGet(
            "/api/meta",
            async (
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                IEntitySchemaCatalog catalog,
                IConfiguration configuration,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var entities = catalog.RawEntities.Select(raw =>
                {
                    var item = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(raw.GetRawText())!;
                    var module = item.TryGetValue("module", out var moduleValue)
                        ? moduleValue.GetString() ?? ""
                        : "";
                    item["mkey"] = JsonSerializer.SerializeToElement(ModuleKeys.GetValueOrDefault(module, module));
                    return item;
                }).ToArray();
                var roles = EfCallerResolver.DefaultRoles.ToDictionary(
                    pair => pair.Key,
                    pair => (object?)pair.Value.Name,
                    StringComparer.Ordinal);
                foreach (var role in await db.SystemRoles.AsNoTracking().ToListAsync(cancellationToken))
                {
                    roles[role.Code ?? ""] = role.Name ?? role.Code ?? "";
                }

                return Results.Json(new
                {
                    entities,
                    roles,
                    lang = RequestLanguageMiddleware.GetLanguage(context),
                    langs = new[] { "en", "zh-CN" },
                    demo = IsEnabled(configuration["MOM_DEMO"])
                });
            });

        endpoints.MapGet(
            "/api/whoami",
            async (
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var scope = ScopeEvaluator.ResolveEffectiveScope(caller!);
                return Results.Json(new
                {
                    username = caller!.Username,
                    name = caller.Name,
                    is_api_key = caller.IsApiKey,
                    roles = caller.Roles.Select(role => role.Code).ToArray(),
                    perms = PermissionPatterns(caller),
                    scope = ScopeResponse(scope),
                    scope_label = await ScopeLabelAsync(
                        db,
                        scope,
                        cancellationToken,
                        RequestLanguageMiddleware.GetLanguage(context))
                });
            });

        return endpoints;
    }

    internal static IReadOnlyList<IReadOnlyList<string>> PermissionPatterns(CallerPrincipal caller)
    {
        var result = caller.Roles
            .Select(role => role.PermissionPatterns)
            .ToList();
        if (caller.AdditionalPermissionPatterns is { Count: > 0 } additional)
        {
            result.Add(additional);
        }

        return result;
    }

    internal static object? ScopeResponse(ScopeSelection? scope) =>
        scope is null
            ? null
            : new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
            {
                ["plants"] = scope.Plants,
                ["workshops"] = scope.Workshops,
                ["lines"] = scope.Lines
            };

    internal static async Task<string> ScopeLabelAsync(
        HoneyMomDbContext db,
        ScopeSelection? scope,
        CancellationToken cancellationToken,
        string language = "en")
    {
        if (scope is null)
        {
            return RequestLanguageMiddleware.Translate("全部", language);
        }

        var names = new List<string>();
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await AddNamesAsync(connection, "plant", scope.Plants, names, cancellationToken);
            await AddNamesAsync(connection, "workshop", scope.Workshops, names, cancellationToken);
            await AddNamesAsync(connection, "line", scope.Lines, names, cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }

        return names.Count == 0
            ? RequestLanguageMiddleware.Translate("（空）", language)
            : string.Join("、", names);
    }

    internal static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray()
            : [];
    }

    private static async Task AddNamesAsync(
        DbConnection connection,
        string table,
        IReadOnlyList<int> ids,
        ICollection<string> names,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var rows = await connection.QueryAsync<string>(
            new CommandDefinition(
                $"SELECT \"name\" FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE \"id\" IN @ids ORDER BY \"id\"",
                new { ids = ids.ToArray() },
                cancellationToken: cancellationToken));
        foreach (var name in rows)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }
    }

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> body, string key) =>
        body.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
