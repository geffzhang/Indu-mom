using System.Globalization;
using System.Text;
using System.Text.Json;
using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Entities;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Api.Endpoints;

public static class EntityEndpoints
{
    private static readonly IReadOnlyDictionary<string, string> ModuleKeys =
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
            ["销售"] = "sales",
            ["采购"] = "purchase",
            ["计划"] = "mrp",
            ["成本"] = "cost",
            ["财务"] = "finance",
            ["ERP"] = "erp"
        };

    public static IEndpointRouteBuilder MapEntityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/e/{entity}.csv",
            async (
                string entity,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityQueryService queryService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                if (!RequirePermission(context, caller!, schema, "read", out var denied))
                {
                    return denied!;
                }

                try
                {
                    var query = BuildQueryRequest(entity, context.Request.Query) with { Page = 1, Size = 50000 };
                    var result = await queryService.ListForExportAsync(query, caller!, cancellationToken);
                    context.Response.Headers.ContentDisposition = $"attachment; filename={entity}.csv";
                    return Results.Text(BuildCsv(schema, result.Items), "text/csv; charset=utf-8");
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        endpoints.MapGet(
            "/api/e/{entity}",
            async (
                string entity,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityQueryService queryService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                if (!RequirePermission(context, caller!, schema, "read", out var denied))
                {
                    return denied!;
                }

                try
                {
                    var result = await queryService.ListAsync(
                        BuildQueryRequest(entity, context.Request.Query),
                        caller!,
                        cancellationToken);
                    return Results.Json(new { total = result.Total, items = result.Items });
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        endpoints.MapGet(
            "/api/e/{entity}/{rid:long}",
            async (
                string entity,
                long rid,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityQueryService queryService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                if (!RequirePermission(context, caller!, schema, "read", out var denied))
                {
                    return denied!;
                }

                try
                {
                    var result = await queryService.GetAsync(entity, rid, caller!, cancellationToken);
                    return result is null
                        ? Error(context, StatusCodes.Status404NotFound, "记录不存在")
                        : Results.Json(new { item = result.Item, children = result.Children });
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        endpoints.MapPost(
            "/api/e/{entity}",
            async (
                string entity,
                Dictionary<string, JsonElement> body,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityQueryService queryService,
                EntityCommandService commandService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                if (schema.Internal)
                {
                    return Error(context, StatusCodes.Status400BadRequest, $"{schema.Label}由系统自动记录，不能手工新增");
                }

                if (!RequirePermission(context, caller!, schema, "create", out var denied))
                {
                    return denied!;
                }

                try
                {
                    var id = await commandService.CreateAsync(entity, body, caller!, cancellationToken);
                    return Results.Json(new { ok = true, id });
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        endpoints.MapPut(
            "/api/e/{entity}/{rid:long}",
            async (
                string entity,
                long rid,
                Dictionary<string, JsonElement> body,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityCommandService commandService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                if (!RequirePermission(context, caller!, schema, "update", out var denied))
                {
                    return denied!;
                }

                try
                {
                    var result = await commandService.UpdateAsync(entity, rid, body, caller!, cancellationToken);
                    return result.Succeeded
                        ? Results.Json(new { ok = true })
                        : Error(context, StatusCodes.Status404NotFound, result.ErrorMessage ?? "记录不存在");
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        endpoints.MapDelete(
            "/api/e/{entity}/{rid:long}",
            async (
                string entity,
                long rid,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityCommandService commandService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                if (!RequirePermission(context, caller!, schema, "delete", out var denied))
                {
                    return denied!;
                }

                try
                {
                    var result = await commandService.DeleteAsync(entity, rid, caller!, cancellationToken);
                    return result.Succeeded
                        ? Results.Json(new { ok = true })
                        : Error(context, StatusCodes.Status404NotFound, result.ErrorMessage ?? "记录不存在");
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        endpoints.MapPost(
            "/api/e/{entity}/{rid:long}/do/{action}",
            async (
                string entity,
                long rid,
                string action,
                Dictionary<string, JsonElement>? body,
                HttpContext context,
                IEntitySchemaCatalog catalog,
                ICallerResolver callerResolver,
                EntityActionDispatcher dispatcher,
                HoneyMomDbContext dbContext,
                EntityQueryService queryService,
                AuditService auditService,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await ResolveCallerAsync(context, callerResolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                var schema = catalog.Get(entity);
                if (schema is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, $"没有实体 {entity}");
                }

                var handler = dispatcher.Find(entity, action);
                if (handler is null)
                {
                    return Error(context, StatusCodes.Status404NotFound, "没有这个操作");
                }

                if (!RequirePermission(context, caller!, schema, action, out var denied))
                {
                    return denied!;
                }

                await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    var row = await ReadActionRowAsync(dbContext, schema, rid, cancellationToken);
                    if (row is null)
                    {
                        return Error(context, StatusCodes.Status404NotFound, "记录不存在");
                    }

                    if (!await queryService.IsInScopeAsync(entity, row, caller!, cancellationToken))
                    {
                        return Error(context, StatusCodes.Status403Forbidden, "不在你的数据范围内");
                    }

                    var result = await handler.ExecuteAsync(
                        new EntityActionContext(
                            entity,
                            rid,
                            row,
                            body ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal),
                            caller!),
                        cancellationToken);
                    await auditService.AppendAsync(
                        dbContext,
                        new HoneyMOM.Application.Auditing.AuditEntry(
                            action,
                            entity,
                            checked((int)rid),
                            User: caller!.Username,
                            Detail: new { action = schema.Actions.GetValueOrDefault(action, action) },
                            Source: "ui"),
                        cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return Results.Json(new Dictionary<string, object?> { ["ok"] = true }
                        .Concat(result)
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
                }
                catch (EntityOperationException exception)
                {
                    return Error(context, exception.StatusCode, exception.Message);
                }
            });

        return endpoints;
    }

    internal static async Task<(CallerPrincipal? Caller, IResult? Error)> ResolveCallerAsync(
        HttpContext context,
        ICallerResolver resolver,
        CancellationToken cancellationToken)
    {
        var credentials = new CallerCredentials(
            context.Request.Headers["X-API-Key"].FirstOrDefault(),
            context.Request.Headers.Authorization.FirstOrDefault(),
            context.Request.Query["token"].FirstOrDefault(),
            context.Request.Cookies["mes_token"],
            context.Connection.RemoteIpAddress?.ToString());
        var result = await resolver.ResolveAsync(credentials, cancellationToken);
        if (result.Succeeded)
        {
            return (result.Caller, null);
        }

        var message = result.Failure switch
        {
            CallerResolutionFailure.InactiveUser => "账号已停用",
            CallerResolutionFailure.InvalidApiKey or CallerResolutionFailure.ApiKeyNotAllowed => "API 密钥无效",
            _ => "请先登录"
        };
        return (null, Error(context, StatusCodes.Status401Unauthorized, message));
    }

    private static bool RequirePermission(
        HttpContext context,
        CallerPrincipal caller,
        EntitySchema schema,
        string operation,
        out IResult? error)
    {
        var module = ModuleKeys.GetValueOrDefault(schema.Module, schema.Module);
        var permission = $"{module}.{schema.Name}.{operation}";
        if (PermissionEvaluator.Allows(caller, permission))
        {
            error = null;
            return true;
        }

        error = Error(context, StatusCodes.Status403Forbidden, $"没有权限：{permission}");
        return false;
    }

    private static EntityQueryRequest BuildQueryRequest(
        string entity,
        IQueryCollection query,
        int size = 20)
    {
        var filters = query
            .Where(item => item.Key.StartsWith("f_", StringComparison.Ordinal))
            .ToDictionary(item => item.Key[2..], item => (string?)item.Value.ToString(), StringComparer.Ordinal);
        return new EntityQueryRequest(
            entity,
            int.TryParse(query["page"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) ? page : 1,
            int.TryParse(query["size"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedSize) ? requestedSize : size,
            query["q"].FirstOrDefault(),
            query["view"].FirstOrDefault(),
            query["sort"].FirstOrDefault(),
            filters);
    }

    private static async Task<IReadOnlyDictionary<string, object?>?> ReadActionRowAsync(
        HoneyMomDbContext dbContext,
        EntitySchema schema,
        long id,
        CancellationToken cancellationToken)
    {
        if (schema.Name == "sys_user")
        {
            var user = await dbContext.SystemUsers.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
            return user is null
                ? null
                : new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["id"] = user.Id,
                    ["username"] = user.Username,
                    ["name"] = user.Name,
                    ["roles"] = user.Roles
                };
        }

        var row = await dbContext.Set<Dictionary<string, object?>>(schema.Name)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => EF.Property<int>(item, "id") == id,
                cancellationToken);
        return row;
    }

    private static string BuildCsv(
        EntitySchema schema,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var output = new StringBuilder("\uFEFF");
        AppendCsvRow(output, schema.Fields.Select(field => field.Label));
        foreach (var row in rows)
        {
            AppendCsvRow(output, schema.Fields.Select(field =>
            {
                if (!row.TryGetValue(field.Name, out var value))
                {
                    return "";
                }

                if (field.Type == "ref" && row.TryGetValue(field.Name + "__label", out var label))
                {
                    value = label;
                }
                else if (field.EnumJson is not null && value is not null)
                {
                    using var values = JsonDocument.Parse(field.EnumJson);
                    var enumValue = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (values.RootElement.TryGetProperty(enumValue ?? "", out var translated))
                    {
                        value = translated.GetString();
                    }
                }

                return value switch
                {
                    null => "",
                    DateTime dateTime => dateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                };
            }));
        }

        return output.ToString();
    }

    private static void AppendCsvRow(StringBuilder output, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values)
        {
            if (!first)
            {
                output.Append(',');
            }

            first = false;
            output.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        output.Append("\r\n");
    }

    internal static IResult Error(HttpContext context, int statusCode, string message) =>
        Results.Json(
            new { detail = RequestLanguageMiddleware.Translate(message, RequestLanguageMiddleware.GetLanguage(context)) },
            statusCode: statusCode);
}
