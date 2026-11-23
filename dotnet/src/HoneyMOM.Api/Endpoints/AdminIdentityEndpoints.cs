using System.Text.Json;
using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Api.Endpoints;

public static class AdminIdentityEndpoints
{
    private const string RoleManagePermission = "system.role.manage";
    private const string ApiKeyManagePermission = "system.apikey.manage";

    private static readonly IReadOnlyDictionary<string, (string Label, string Group)> Features =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["system.role.manage"] = ("角色与权限、用户数据范围", "系统"),
            ["system.audit.view"] = ("查看审计日志", "系统"),
            ["system.audit.export"] = ("导出审计日志", "系统"),
            ["system.apikey.manage"] = ("API 密钥管理", "系统"),
            ["system.backup.view"] = ("查看备份", "系统"),
            ["system.backup.manage"] = ("备份、恢复演练、删除备份", "系统"),
            ["system.db.view"] = ("数据库与迁移状态", "系统"),
            ["system.ai.manage"] = ("AI 功能开关、演示录音", "系统"),
            ["system.daily.schedule"] = ("设置生产日报定时", "系统"),
            ["ai.agent.edit"] = ("新建/编辑/发布智能体", "AI 助手"),
            ["equipment.ts.read"] = ("读设备时序数据", "设备管理"),
            ["equipment.ts.write"] = ("写入设备时序数据（网关 HTTP 推送）", "设备管理")
        };

    public static IEndpointRouteBuilder MapAdminIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/admin/perms/catalog",
            async (
                HttpContext context,
                ICallerResolver resolver,
                IEntitySchemaCatalog catalog,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return EntityEndpoints.Error(
                        context,
                        StatusCodes.Status403Forbidden,
                        $"没有权限：{RoleManagePermission}");
                }

                var entities = catalog.Entities
                    .Where(entity => PlatformEndpoints.ModuleKeys.ContainsKey(entity.Module))
                    .Select(entity =>
                    {
                        var operations = entity.Internal
                            ? new[] { "read" }
                            : new[] { "read", "create", "update", "delete" };
                        var actions = entity.Actions.ToList();
                        if (entity.Name == "sys_user"
                            && !actions.Any(action => action.Key == "reset_pw"))
                        {
                            actions.Add(new KeyValuePair<string, string>("reset_pw", "重置密码"));
                        }

                        var permissions = operations.Select(operation => (object)new
                            {
                                id = $"{PlatformEndpoints.ModuleKeys[entity.Module]}.{entity.Name}.{operation}",
                                label = OperationLabels[operation],
                                kind = "api"
                            })
                            .Concat(actions.Select(action => (object)new
                            {
                                id = $"{PlatformEndpoints.ModuleKeys[entity.Module]}.{entity.Name}.{action.Key}",
                                label = action.Value,
                                kind = "action"
                            }))
                            .ToArray();
                        return new
                        {
                            entity = entity.Name,
                            label = entity.Label,
                            module = PlatformEndpoints.ModuleKeys[entity.Module],
                            module_label = entity.Module,
                            module_name = entity.Module,
                            @internal = entity.Internal,
                            perms = permissions
                        };
                    })
                    .ToArray();
                var modules = PlatformEndpoints.ModuleKeys
                    .Select(pair => new
                    {
                        key = pair.Value,
                        label = pair.Key,
                        module_label = pair.Key,
                        module_name = pair.Key
                    })
                    .ToArray();
                var features = Features.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new
                    {
                        id = pair.Key,
                        label = RequestLanguageMiddleware.Translate(pair.Value.Label, RequestLanguageMiddleware.GetLanguage(context)),
                        group = RequestLanguageMiddleware.Translate(pair.Value.Group, RequestLanguageMiddleware.GetLanguage(context)),
                        kind = "feature"
                    })
                    .ToArray();
                return Results.Json(new { entities, features, modules });
            });

        endpoints.MapGet(
            "/api/admin/roles",
            async (
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                var rows = await db.SystemRoles.AsNoTracking()
                    .OrderByDescending(role => role.Builtin)
                    .ThenBy(role => role.Id)
                    .ToListAsync(cancellationToken);
                var counts = await RoleCountsAsync(db, cancellationToken);
                var items = new List<object>(rows.Count);
                foreach (var role in rows)
                {
                    items.Add(await RoleOutputAsync(
                        db,
                        role,
                        counts,
                        cancellationToken,
                        RequestLanguageMiddleware.GetLanguage(context)));
                }

                return Results.Json(new
                {
                    items,
                    defaults = EfCallerResolver.DefaultRoles.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Permissions,
                        StringComparer.Ordinal)
                });
            });

        endpoints.MapPost(
            "/api/admin/roles",
            async (
                Dictionary<string, JsonElement> body,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                var code = (GetString(body, "code") ?? "").Trim().ToLowerInvariant();
                if (!IsValidRoleCode(code))
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "角色编码只能用小写字母、数字和下划线");
                }

                var name = (GetString(body, "name") ?? "").Trim();
                if (name.Length == 0)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "请填写角色名称");
                }

                if (await db.SystemRoles.AnyAsync(role => role.Code == code, cancellationToken))
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, $"角色 {code} 已存在");
                }

                var now = DateTime.Now;
                var role = new SystemRoleRow
                {
                    Code = code,
                    Name = name[..Math.Min(name.Length, 80)],
                    Perms = JsonSerializer.Serialize(CleanPermissions(body.GetValueOrDefault("perms"))),
                    Scope = SerializeScope(NormalizeScope(body.GetValueOrDefault("scope"))),
                    Builtin = false,
                    Remark = (GetString(body, "remark") ?? "")[..Math.Min((GetString(body, "remark") ?? "").Length, 200)],
                    CreatedAt = now,
                    UpdatedAt = now
                };
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                db.SystemRoles.Add(role);
                await db.SaveChangesAsync(cancellationToken);
                await audit.AppendAsync(
                    db,
                    new AuditEntry(
                        "create",
                        "sys_role",
                        role.Id,
                        Diff: new { after = RoleSnapshot(role) },
                        Target: $"role:{code}",
                        User: caller!.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new { ok = true, code });
            });

        endpoints.MapPut(
            "/api/admin/roles/{code}",
            async (
                string code,
                Dictionary<string, JsonElement> body,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                return await UpdateRoleAsync(code, body, caller!, context, db, audit, cancellationToken);
            });

        endpoints.MapPost(
            "/api/admin/roles/{code}/reset",
            async (
                string code,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                if (!EfCallerResolver.DefaultRoles.TryGetValue(code, out var defaults))
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "只有内置角色能恢复默认");
                }

                var body = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["perms"] = JsonSerializer.SerializeToElement(defaults.Permissions),
                    ["scope"] = JsonSerializer.SerializeToElement<object?>(null)
                };
                return await UpdateRoleAsync(code, body, caller!, context, db, audit, cancellationToken);
            });

        endpoints.MapDelete(
            "/api/admin/roles/{code}",
            async (
                string code,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                var role = await db.SystemRoles.SingleOrDefaultAsync(row => row.Code == code, cancellationToken);
                if (role is null)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status404NotFound, "没有这个角色");
                }

                if (role.Builtin == true)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "内置角色不能删除（可以改权限）");
                }

                var counts = await RoleCountsAsync(db, cancellationToken);
                if (counts.GetValueOrDefault(code) is var count && count > 0)
                {
                    return EntityEndpoints.Error(
                        context,
                        StatusCodes.Status400BadRequest,
                        $"还有 {count} 个用户用这个角色，先把他们换成别的角色");
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var snapshot = RoleSnapshot(role);
                db.SystemRoles.Remove(role);
                await db.SaveChangesAsync(cancellationToken);
                await audit.AppendAsync(
                    db,
                    new AuditEntry(
                        "delete",
                        "sys_role",
                        role.Id,
                        Diff: new { before = snapshot },
                        Target: $"role:{code}",
                        User: caller!.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new { ok = true });
            });

        endpoints.MapGet(
            "/api/admin/users",
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

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                var rows = await db.SystemUsers.AsNoTracking()
                    .OrderBy(user => user.Id)
                    .ToListAsync(cancellationToken);
                var items = new List<object>(rows.Count);
                foreach (var user in rows)
                {
                    var scope = ReadScope(user.Scope);
                    items.Add(new
                    {
                        id = user.Id,
                        username = user.Username,
                        name = user.Name,
                        roles = user.Roles,
                        active = user.Active,
                        scope = PlatformEndpoints.ScopeResponse(scope),
                        scope_label = await PlatformEndpoints.ScopeLabelAsync(
                            db,
                            scope,
                            cancellationToken,
                            RequestLanguageMiddleware.GetLanguage(context)),
                        role_list = SplitRoles(user.Roles)
                    });
                }

                return Results.Json(new { items });
            });

        endpoints.MapPut(
            "/api/admin/users/{uid:int}",
            async (
                int uid,
                Dictionary<string, JsonElement> body,
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission))
                {
                    return Forbidden(context, RoleManagePermission);
                }

                var user = await db.SystemUsers.SingleOrDefaultAsync(row => row.Id == uid, cancellationToken);
                if (user is null)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status404NotFound, "没有这个用户");
                }

                var previousRoleText = user.Roles;
                var previousScope = ReadScope(user.Scope);
                var previousScopeJson = SerializeScope(previousScope is null
                    ? null
                    : new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                    {
                        ["plants"] = previousScope.Plants,
                        ["workshops"] = previousScope.Workshops,
                        ["lines"] = previousScope.Lines
                    });
                if (body.TryGetValue("roles", out var rolesValue))
                {
                    var roleCodes = ParseRoles(rolesValue);
                    var allowedRoles = await GetAllowedRoleCodesAsync(db, cancellationToken);
                    var unknown = roleCodes.Where(role => !allowedRoles.Contains(role, StringComparer.Ordinal)).ToArray();
                    if (unknown.Length > 0)
                    {
                        return EntityEndpoints.Error(
                            context,
                            StatusCodes.Status400BadRequest,
                            $"没有这些角色：{string.Join("、", unknown)}");
                    }

                    var previousRoles = SplitRoles(user.Roles);
                    if (user.Username == caller!.Username
                        && previousRoles.Contains("admin", StringComparer.Ordinal)
                        && !roleCodes.Contains("admin", StringComparer.Ordinal))
                    {
                        return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, "不能去掉自己的管理员角色");
                    }

                    user.Roles = string.Join(',', roleCodes);
                }

                if (body.TryGetValue("scope", out var scopeValue))
                {
                    user.Scope = SerializeScope(NormalizeScope(scopeValue));
                }

                user.UpdatedAt = DateTime.Now;
                var updatedScope = ReadScope(user.Scope);
                var updatedScopeJson = SerializeScope(updatedScope is null
                    ? null
                    : new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal)
                    {
                        ["plants"] = updatedScope.Plants,
                        ["workshops"] = updatedScope.Workshops,
                        ["lines"] = updatedScope.Lines
                    });
                var diff = new Dictionary<string, object?>(StringComparer.Ordinal);
                if (previousRoleText != user.Roles)
                {
                    diff["roles"] = new object?[] { previousRoleText, user.Roles };
                }

                if (previousScopeJson != updatedScopeJson)
                {
                    diff["scope"] = new object?[]
                    {
                        PlatformEndpoints.ScopeResponse(previousScope),
                        PlatformEndpoints.ScopeResponse(updatedScope)
                    };
                }

                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                if (diff.Count > 0)
                {
                    await audit.AppendAsync(
                        db,
                        new AuditEntry(
                            "update",
                            "sys_user",
                            user.Id,
                            Diff: diff,
                            User: caller!.Username),
                        cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return Results.Json(new { ok = true });
            });

        endpoints.MapGet(
            "/api/admin/org",
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

                if (!PermissionEvaluator.Allows(caller!, RoleManagePermission)
                    && !PermissionEvaluator.Allows(caller!, ApiKeyManagePermission))
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status403Forbidden, "没有权限");
                }

                var plants = await db.Set<Dictionary<string, object?>>("plant").AsNoTracking()
                    .OrderBy(row => EF.Property<int>(row, "id"))
                    .ToListAsync(cancellationToken);
                var workshops = await db.Set<Dictionary<string, object?>>("workshop").AsNoTracking()
                    .OrderBy(row => EF.Property<int>(row, "id"))
                    .ToListAsync(cancellationToken);
                var lines = await db.Set<Dictionary<string, object?>>("line").AsNoTracking()
                    .OrderBy(row => EF.Property<int>(row, "id"))
                    .ToListAsync(cancellationToken);

                var plantItems = plants.Select(plant =>
                {
                    var plantId = GetInt(plant, "id");
                    return new
                    {
                        id = plantId,
                        code = GetValue(plant, "code"),
                        name = GetValue(plant, "name"),
                        workshops = workshops
                            .Where(workshop => GetNullableInt(workshop, "plant") == plantId)
                            .Select(workshop =>
                            {
                                var workshopId = GetInt(workshop, "id");
                                return new
                                {
                                    id = workshopId,
                                    code = GetValue(workshop, "code"),
                                    name = GetValue(workshop, "name"),
                                    lines = lines
                                        .Where(line => GetNullableInt(line, "workshop") == workshopId)
                                        .Select(line => new
                                        {
                                            id = GetInt(line, "id"),
                                            code = GetValue(line, "code"),
                                            name = GetValue(line, "name")
                                        })
                                        .ToArray()
                                };
                            })
                            .ToArray()
                    };
                }).ToArray();
                var orphans = new
                {
                    workshops = workshops
                        .Where(workshop => GetNullableInt(workshop, "plant") is null or 0)
                        .Select(workshop => new
                        {
                            id = GetInt(workshop, "id"),
                            name = GetValue(workshop, "name")
                        })
                        .ToArray(),
                    lines = lines
                        .Where(line => GetNullableInt(line, "workshop") is null or 0)
                        .Select(line => new
                        {
                            id = GetInt(line, "id"),
                            name = GetValue(line, "name")
                        })
                        .ToArray()
                };
                return Results.Json(new { plants = plantItems, orphans });
            });

        return endpoints;
    }

    private static readonly IReadOnlyDictionary<string, string> OperationLabels =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["read"] = "查看",
            ["create"] = "新增",
            ["update"] = "修改",
            ["delete"] = "删除"
        };

    private static async Task<IResult> UpdateRoleAsync(
        string code,
        IReadOnlyDictionary<string, JsonElement> body,
        CallerPrincipal caller,
        HttpContext context,
        HoneyMomDbContext db,
        AuditService audit,
        CancellationToken cancellationToken)
    {
        var role = await db.SystemRoles.SingleOrDefaultAsync(row => row.Code == code, cancellationToken);
        if (role is null)
        {
            return EntityEndpoints.Error(context, StatusCodes.Status404NotFound, "没有这个角色");
        }

        var before = RoleSnapshot(role);
        if (body.TryGetValue("name", out var nameValue))
        {
            var name = (nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString() : "")?.Trim() ?? "";
            if (name.Length > 0)
            {
                role.Name = name[..Math.Min(name.Length, 80)];
            }
        }

        if (body.TryGetValue("perms", out var permissionsValue))
        {
            var permissions = CleanPermissions(permissionsValue);
            if (code == "admin" && !permissions.Contains("*", StringComparer.Ordinal))
            {
                return EntityEndpoints.Error(
                    context,
                    StatusCodes.Status400BadRequest,
                    "系统管理员角色必须保留全部权限（*），否则可能没人能再管理权限");
            }

            role.Perms = JsonSerializer.Serialize(permissions);
        }

        if (body.TryGetValue("scope", out var scopeValue))
        {
            role.Scope = SerializeScope(NormalizeScope(scopeValue));
        }

        if (body.TryGetValue("remark", out var remarkValue))
        {
            var remark = remarkValue.ValueKind == JsonValueKind.String ? remarkValue.GetString() ?? "" : "";
            role.Remark = remark[..Math.Min(remark.Length, 200)];
        }

        role.UpdatedAt = DateTime.Now;
        var after = RoleSnapshot(role);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.AppendAsync(
            db,
            new AuditEntry(
                "update",
                "sys_role",
                role.Id,
                Diff: CreateDiff(before, after),
                Target: $"role:{code}",
                User: caller.Username),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Json(new { ok = true });
    }

    private static async Task<object> RoleOutputAsync(
        HoneyMomDbContext db,
        SystemRoleRow role,
        IReadOnlyDictionary<string, int> counts,
        CancellationToken cancellationToken,
        string language)
    {
        var scope = ReadScope(role.Scope);
        return new
        {
            code = role.Code,
            name = role.Name,
            perms = PlatformEndpoints.ParseStringArray(role.Perms),
            scope = PlatformEndpoints.ScopeResponse(scope),
            scope_label = await PlatformEndpoints.ScopeLabelAsync(db, scope, cancellationToken, language),
            builtin = role.Builtin == true,
            remark = role.Remark ?? "",
            users = counts.GetValueOrDefault(role.Code ?? ""),
            updated_at = role.UpdatedAt
        };
    }

    private static async Task<Dictionary<string, int>> RoleCountsAsync(
        HoneyMomDbContext db,
        CancellationToken cancellationToken)
    {
        var roleStrings = await db.SystemUsers.AsNoTracking()
            .Select(user => user.Roles)
            .ToListAsync(cancellationToken);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var role in roleStrings.SelectMany(SplitRoles))
        {
            counts[role] = counts.GetValueOrDefault(role) + 1;
        }

        return counts;
    }

    private static async Task<HashSet<string>> GetAllowedRoleCodesAsync(
        HoneyMomDbContext db,
        CancellationToken cancellationToken)
    {
        var roles = await db.SystemRoles.AsNoTracking()
            .Select(role => role.Code)
            .ToListAsync(cancellationToken);
        return roles.Count == 0
            ? EfCallerResolver.DefaultRoles.Keys.ToHashSet(StringComparer.Ordinal)
            : roles.Where(code => !string.IsNullOrWhiteSpace(code)).Select(code => code!).ToHashSet(StringComparer.Ordinal);
    }

    private static IReadOnlyList<string> CleanPermissions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var permission = item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? ""
                : item.ToString();
            permission = permission.Trim();
            if (permission.Length > 0
                && permission.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '*' or '!')
                && !result.Contains(permission, StringComparer.Ordinal))
            {
                result.Add(permission);
            }
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<int>>? NormalizeScope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scope = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var key in new[] { "plants", "workshops", "lines" })
        {
            var ids = new SortedSet<int>();
            if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in field.EnumerateArray())
                {
                    var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString();
                    if (int.TryParse(text, out var id))
                    {
                        ids.Add(id);
                    }
                }
            }

            scope[key] = ids.ToArray();
        }

        return scope.Values.Any(ids => ids.Count > 0) ? scope : null;
    }

    private static string? SerializeScope(Dictionary<string, IReadOnlyList<int>>? scope) =>
        scope is null ? null : JsonSerializer.Serialize(scope);

    private static ScopeSelection? ReadScope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        var normalized = NormalizeScope(document.RootElement);
        return normalized is null
            ? null
            : new ScopeSelection(normalized["plants"], normalized["workshops"], normalized["lines"]);
    }

    private static IReadOnlyList<string> ParseRoles(JsonElement value)
    {
        var values = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            : (value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "")
                .Split(',', StringSplitOptions.None)
                .AsEnumerable();
        return values
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToArray();
    }

    private static IReadOnlyList<string> SplitRoles(string? value) =>
        (value ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsValidRoleCode(string code) =>
        code.Length is > 0 and <= 40
        && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static object RoleSnapshot(SystemRoleRow role) => new
    {
        code = role.Code,
        name = role.Name,
        perms = PlatformEndpoints.ParseStringArray(role.Perms),
        scope = ReadScope(role.Scope),
        builtin = role.Builtin,
        remark = role.Remark
    };

    private static object CreateDiff(object before, object after) => new { before, after };

    private static string? GetString(IReadOnlyDictionary<string, JsonElement> body, string key) =>
        body.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IResult Forbidden(HttpContext context, string permission) =>
        EntityEndpoints.Error(context, StatusCodes.Status403Forbidden, $"没有权限：{permission}");

    private static object? GetValue(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value : null;

    private static int GetInt(IReadOnlyDictionary<string, object?> row, string key) =>
        Convert.ToInt32(GetValue(row, key), System.Globalization.CultureInfo.InvariantCulture);

    private static int? GetNullableInt(IReadOnlyDictionary<string, object?> row, string key)
    {
        var value = GetValue(row, key);
        return value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
