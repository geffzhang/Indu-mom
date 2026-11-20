using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Api.Endpoints;

public static class AdminAuditEndpoints
{
    private const string ViewPermission = "system.audit.view";
    private const string ExportPermission = "system.audit.export";

    private static readonly IReadOnlyDictionary<string, string> Sources =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ui"] = "界面",
            ["api"] = "接口",
            ["ai"] = "AI",
            ["agent"] = "智能体",
            ["import"] = "导入",
            ["collector"] = "采集",
            ["system"] = "系统",
            ["migration"] = "迁移"
        };

    public static IEndpointRouteBuilder MapAdminAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/admin/audit",
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

                if (!PermissionEvaluator.Allows(caller!, ViewPermission))
                {
                    return Forbidden(context, ViewPermission);
                }

                await audit.SealAsync(db, cancellationToken);
                var page = ParsePositiveInt(context.Request.Query["page"], 1);
                var size = Math.Clamp(ParsePositiveInt(context.Request.Query["size"], 50), 1, 500);
                long total;
                IReadOnlyList<AuditRow> rows;
                try
                {
                    (total, rows) = await QueryAsync(
                        db,
                        context.Request.Query,
                        size,
                        checked((page - 1) * size),
                        cancellationToken);
                }
                catch (AuditQueryException exception)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, exception.Message);
                }

                return Results.Json(new
                {
                    total,
                    items = rows.Select(ToResponse).ToArray(),
                    sources = Sources
                });
            });

        endpoints.MapGet(
            "/api/admin/audit/facets",
            async (
                HttpContext context,
                ICallerResolver resolver,
                HoneyMomDbContext db,
                AuditService audit,
                IEntitySchemaCatalog catalog,
                CancellationToken cancellationToken) =>
            {
                var (caller, error) = await EntityEndpoints.ResolveCallerAsync(context, resolver, cancellationToken);
                if (error is not null)
                {
                    return error;
                }

                if (!PermissionEvaluator.Allows(caller!, ViewPermission))
                {
                    return Forbidden(context, ViewPermission);
                }

                await audit.SealAsync(db, cancellationToken);
                var users = await DistinctAsync(db, "\"user\"", cancellationToken);
                var entityNames = await DistinctAsync(db, "entity", cancellationToken);
                var actions = await DistinctAsync(db, "kind", cancellationToken);
                var entities = entityNames
                    .Order(StringComparer.Ordinal)
                    .Select(name => new { name, label = catalog.Get(name)?.Label ?? name })
                    .ToArray();
                return Results.Json(new
                {
                    users = users.Order(StringComparer.Ordinal).ToArray(),
                    entities,
                    actions = actions.Order(StringComparer.Ordinal).ToArray(),
                    sources = Sources
                });
            });

        endpoints.MapGet(
            "/api/admin/audit/verify",
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

                if (!PermissionEvaluator.Allows(caller!, ViewPermission))
                {
                    return Forbidden(context, ViewPermission);
                }

                var verification = await audit.VerifyAsync(db, cancellationToken);
                var problem = verification.Problem is null
                    ? null
                    : new
                    {
                        seq = verification.Problem.Sequence,
                        id = verification.Problem.Id,
                        reason = verification.Problem.Reason
                    };
                return Results.Json(new
                {
                    ok = verification.Ok,
                    @checked = verification.Checked,
                    unsealed = verification.Unsealed,
                    problem
                });
            });

        endpoints.MapGet(
            "/api/admin/audit.csv",
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

                if (!PermissionEvaluator.Allows(caller!, ExportPermission))
                {
                    return Forbidden(context, ExportPermission);
                }

                await audit.SealAsync(db, cancellationToken);
                var filters = context.Request.Query;
                IReadOnlyList<AuditRow> rows;
                try
                {
                    (_, rows) = await QueryAsync(db, filters, 100_000, 0, cancellationToken);
                }
                catch (AuditQueryException exception)
                {
                    return EntityEndpoints.Error(context, StatusCodes.Status400BadRequest, exception.Message);
                }

                var language = RequestLanguageMiddleware.GetLanguage(context);
                var csv = BuildCsv(rows, language);
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                await audit.AppendAsync(
                    db,
                    new HoneyMOM.Application.Auditing.AuditEntry(
                        "audit_export",
                        Detail: new { rows = rows.Count, filters = filters.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()) },
                        Target: "audit_log",
                        User: caller!.Username),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                context.Response.Headers.ContentDisposition =
                    $"attachment; filename=audit-{DateTime.Now:yyyyMMdd-HHmm}.csv";
                return Results.Text(csv, "text/csv; charset=utf-8", Encoding.UTF8);
            });

        return endpoints;
    }

    private static async Task<(long Total, IReadOnlyList<AuditRow> Rows)> QueryAsync(
        HoneyMomDbContext db,
        IQueryCollection filters,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();
        AddExactFilter(filters, conditions, parameters, "user", "\"user\"");
        AddExactFilter(filters, conditions, parameters, "entity", "entity");
        AddExactFilter(filters, conditions, parameters, "source", "source");
        if (!string.IsNullOrWhiteSpace(filters["entity_id"]))
        {
            if (!int.TryParse(filters["entity_id"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var entityId))
            {
                throw new AuditQueryException("entity_id must be an integer");
            }

            conditions.Add("entity_id = @entity_id");
            parameters.Add("entity_id", entityId);
        }

        if (!string.IsNullOrWhiteSpace(filters["action"]))
        {
            conditions.Add("(action = @action OR kind = @action)");
            parameters.Add("action", filters["action"].ToString());
        }

        AddDateFilter(filters, conditions, parameters, "start", ">=");
        AddDateFilter(filters, conditions, parameters, "end", "<");
        if (!string.IsNullOrWhiteSpace(filters["q"]))
        {
            conditions.Add("(LOWER(target) LIKE @query OR LOWER(kind) LIKE @query OR LOWER(\"user\") LIKE @query)");
            parameters.Add("query", $"%{filters["q"].ToString().ToLowerInvariant()}%");
        }

        var where = conditions.Count == 0 ? "" : $" WHERE {string.Join(" AND ", conditions)}";
        parameters.Add("limit", limit);
        parameters.Add("offset", offset);
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var total = await connection.QuerySingleAsync<long>(
                new CommandDefinition(
                    $"SELECT COUNT(*) FROM audit_log{where}",
                    parameters,
                    cancellationToken: cancellationToken));
            var rows = await connection.QueryAsync<AuditRow>(
                new CommandDefinition(
                    $"SELECT id AS Id, seq AS Seq, time AS Time, \"user\" AS UserName, kind AS Kind, " +
                    $"target AS Target, entity AS Entity, entity_id AS EntityId, action AS Action, diff AS Diff, " +
                    $"detail AS Detail, source AS Source, ip AS Ip, hash AS Hash FROM audit_log{where} " +
                    "ORDER BY id DESC LIMIT @limit OFFSET @offset",
                    parameters,
                    cancellationToken: cancellationToken));
            return (total, rows.AsList());
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<IReadOnlyList<string>> DistinctAsync(
        HoneyMomDbContext db,
        string column,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var values = await connection.QueryAsync<string>(
                new CommandDefinition(
                    $"SELECT DISTINCT {column} FROM audit_log WHERE {column} IS NOT NULL LIMIT 200",
                    cancellationToken: cancellationToken));
            return values.Where(value => !string.IsNullOrEmpty(value)).ToArray();
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static void AddExactFilter(
        IQueryCollection filters,
        ICollection<string> conditions,
        DynamicParameters parameters,
        string parameterName,
        string column)
    {
        if (!string.IsNullOrWhiteSpace(filters[parameterName]))
        {
            conditions.Add($"{column} = @{parameterName}");
            parameters.Add(parameterName, filters[parameterName].ToString());
        }
    }

    private static void AddDateFilter(
        IQueryCollection filters,
        ICollection<string> conditions,
        DynamicParameters parameters,
        string parameterName,
        string comparison)
    {
        if (string.IsNullOrWhiteSpace(filters[parameterName]))
        {
            return;
        }

        if (!DateTime.TryParse(
            filters[parameterName],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
            out var value))
        {
            throw new AuditQueryException($"{parameterName} must be a date or datetime");
        }

        conditions.Add($"time {comparison} @{parameterName}");
        parameters.Add(parameterName, value);
    }

    private static object ToResponse(AuditRow row) => new
    {
        id = row.Id,
        seq = row.Seq,
        time = row.Time,
        user = row.UserName,
        kind = row.Kind,
        action = row.Action ?? row.Kind,
        entity = row.Entity,
        entity_id = row.EntityId,
        target = row.Target,
        diff = ParseJson(row.Diff),
        detail = ParseJson(row.Detail),
        source = row.Source,
        ip = row.Ip,
        hash = row.Hash
    };

    private static object? ParseJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static string BuildCsv(IReadOnlyList<AuditRow> rows, string language)
    {
        var headers = new[]
        {
            "序号", "时间", "用户", "动作", "对象", "对象ID", "目标", "来源", "IP", "改动", "详情", "哈希"
        }.Select(header => RequestLanguageMiddleware.Translate(header, language)).ToArray();
        var output = new StringBuilder("\uFEFF");
        AppendCsvRow(output, headers);
        foreach (var row in rows)
        {
            AppendCsvRow(output,
            [
                row.Seq?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.Time?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "",
                row.UserName ?? "",
                row.Action ?? row.Kind ?? "",
                row.Entity ?? "",
                row.EntityId?.ToString(CultureInfo.InvariantCulture) ?? "",
                row.Target ?? "",
                Sources.GetValueOrDefault(row.Source ?? "", row.Source ?? ""),
                row.Ip ?? "",
                JsonForCsv(row.Diff),
                JsonForCsv(row.Detail),
                row.Hash ?? ""
            ]);
        }

        return output.ToString();
    }

    private static string JsonForCsv(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : FormatJsonForCsv(value);

    private static string FormatJsonForCsv(string value)
    {
        using var document = JsonDocument.Parse(value);
        return JsonSerializer.Serialize(document.RootElement);
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
            if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
            {
                output.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                output.Append(value);
            }
        }

        output.Append("\r\n");
    }

    private static int ParsePositiveInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result > 0
            ? result
            : fallback;

    private static IResult Forbidden(HttpContext context, string permission) =>
        EntityEndpoints.Error(context, StatusCodes.Status403Forbidden, $"没有权限：{permission}");

    private sealed class AuditRow
    {
        public int Id { get; set; }
        public int? Seq { get; set; }
        public DateTime? Time { get; set; }
        public string? UserName { get; set; }
        public string? Kind { get; set; }
        public string? Target { get; set; }
        public string? Entity { get; set; }
        public int? EntityId { get; set; }
        public string? Action { get; set; }
        public string? Diff { get; set; }
        public string? Detail { get; set; }
        public string? Source { get; set; }
        public string? Ip { get; set; }
        public string? Hash { get; set; }
    }
}

public sealed class AuditQueryException(string message) : Exception(message);
