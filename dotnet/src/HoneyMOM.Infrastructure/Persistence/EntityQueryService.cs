using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using HoneyMOM.Application.Entities;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Application.Security;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class EntityQueryService
{
    private static readonly string[] ScopeTargets = ["line", "workshop", "plant", "equipment", "workstation"];
    private static readonly IReadOnlyDictionary<string, (string Field, string Target)> ScopeBindings =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["sales_order"] = ("plant", "plant"),
            ["delivery_order"] = ("plant", "plant"),
            ["sales_return"] = ("plant", "plant"),
            ["so_line"] = ("sales_order", "sales_order"),
            ["delivery_line"] = ("delivery_order", "delivery_order"),
            ["purchase_order"] = ("plant", "plant"),
            ["purchase_receipt"] = ("plant", "plant"),
            ["po_line"] = ("purchase_order", "purchase_order"),
            ["work_order"] = ("line", "line")
        };

    private readonly HoneyMomDbContext _context;
    private readonly IEntitySchemaCatalog _catalog;

    public EntityQueryService(HoneyMomDbContext context, IEntitySchemaCatalog? catalog = null)
    {
        _context = context;
        _catalog = catalog ?? HoneyMOM.Infrastructure.Persistence.Metadata.SchemaSnapshotCatalog.Default;
    }

    public async Task<EntityQueryResult> ListAsync(
        EntityQueryRequest request,
        CallerPrincipal caller,
        CancellationToken cancellationToken) =>
        await ListAsync(request, caller, 2000, cancellationToken);

    public async Task<EntityQueryResult> ListForExportAsync(
        EntityQueryRequest request,
        CallerPrincipal caller,
        CancellationToken cancellationToken) =>
        await ListAsync(request, caller, 50000, cancellationToken);

    private async Task<EntityQueryResult> ListAsync(
        EntityQueryRequest request,
        CallerPrincipal caller,
        int maximumSize,
        CancellationToken cancellationToken)
    {
        var entity = RequireEntity(request.Entity);
        if (!string.IsNullOrWhiteSpace(request.View))
        {
            throw new EntityOperationException(400, $"没有视图 {request.View}");
        }

        var parameters = new DynamicParameters();
        var conditions = new List<string>();
        var scope = await ResolveScopeAsync(caller, cancellationToken);
        AddScopeCondition(entity, scope, conditions, parameters);
        AddSearchCondition(entity, request.Query, conditions, parameters);
        AddFilterConditions(entity, request.Filters, conditions, parameters);

        var where = conditions.Count == 0 ? "" : $" WHERE {string.Join(" AND ", conditions)}";
        var table = Quote(entity.Name);
        var connection = _context.Database.GetDbConnection();
        var closeConnection = await OpenConnectionAsync(connection, cancellationToken);
        try
        {
            var total = await connection.QuerySingleAsync<long>(
                new CommandDefinition($"SELECT COUNT(*) FROM {table}{where}", parameters, cancellationToken: cancellationToken));

            var sort = ResolveSort(entity, request.Sort);
            var descending = sort.Descending ? "DESC" : "ASC";
            var id = Quote(RequireColumn(entity, "id"));
            var page = Math.Max(1, request.Page);
            var size = Math.Clamp(request.Size, 1, maximumSize);
            parameters.Add("limit", size);
            parameters.Add("offset", (long)(page - 1) * size);
            var sql = $"SELECT * FROM {table}{where} ORDER BY {Quote(sort.Column)} {descending}, {id} {descending} LIMIT @limit OFFSET @offset";
            var items = await QueryRowsAsync(connection, sql, parameters, cancellationToken);
            await AttachLabelsAsync(connection, entity, items, cancellationToken);
            return new EntityQueryResult(total, items);
        }
        finally
        {
            await CloseConnectionAsync(connection, closeConnection);
        }
    }

    public async Task<EntityDetailResult?> GetAsync(
        string entityName,
        long id,
        CallerPrincipal caller,
        CancellationToken cancellationToken)
    {
        var entity = RequireEntity(entityName);
        var connection = _context.Database.GetDbConnection();
        var closeConnection = await OpenConnectionAsync(connection, cancellationToken);
        try
        {
            var row = await QuerySingleRowAsync(
                connection,
                $"SELECT * FROM {Quote(entity.Name)} WHERE {Quote(RequireColumn(entity, "id"))} = @id",
                new { id },
                cancellationToken);
            if (row is null)
            {
                return null;
            }

            if (!await IsInScopeAsync(entity, row, caller, connection, cancellationToken))
            {
                throw new EntityOperationException(404, "记录不存在");
            }

            var itemRows = new List<Dictionary<string, object?>> { row };
            await AttachLabelsAsync(connection, entity, itemRows, cancellationToken);
            var children = new Dictionary<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>>(StringComparer.Ordinal);
            foreach (var childLink in entity.Children)
            {
                var child = RequireEntity(childLink.Entity);
                var foreignKey = RequireColumn(child, childLink.ForeignKey);
                var parentKey = childLink.ParentKey is null
                    ? "id"
                    : RequireColumn(entity, childLink.ParentKey);
                var parentValue = row.TryGetValue(parentKey, out var value) ? value : null;
                var orderColumn = child.Table.Columns.Any(column => column.Name == "seq") ? "seq" : "id";
                var order = orderColumn == "id" ? $"{Quote(orderColumn)} DESC" : Quote(orderColumn);
                var childRows = await QueryRowsAsync(
                    connection,
                    $"SELECT * FROM {Quote(child.Name)} WHERE {Quote(foreignKey)} = @parentValue ORDER BY {order} LIMIT 500",
                    new { parentValue },
                    cancellationToken);
                await AttachLabelsAsync(connection, child, childRows, cancellationToken);
                children[child.Name] = childRows;
            }

            return new EntityDetailResult(itemRows[0], children);
        }
        finally
        {
            await CloseConnectionAsync(connection, closeConnection);
        }
    }

    public async Task<bool> IsInScopeAsync(
        string entityName,
        IReadOnlyDictionary<string, object?> row,
        CallerPrincipal caller,
        CancellationToken cancellationToken)
    {
        var entity = RequireEntity(entityName);
        var scope = ScopeEvaluator.ResolveEffectiveScope(caller);
        if (scope is null)
        {
            return true;
        }

        var connection = _context.Database.GetDbConnection();
        var closeConnection = await OpenConnectionAsync(connection, cancellationToken);
        try
        {
            return await IsInScopeAsync(entity, row, caller, connection, cancellationToken);
        }
        finally
        {
            await CloseConnectionAsync(connection, closeConnection);
        }
    }

    internal EntitySchema RequireEntity(string entityName) =>
        _catalog.Get(entityName) ?? throw new EntityOperationException(404, $"没有实体 {entityName}");

    internal static string RequireColumn(EntitySchema entity, string name) =>
        entity.Table.Columns.Any(column => string.Equals(column.Name, name, StringComparison.Ordinal))
            ? name
            : throw new EntityOperationException(400, $"未知字段 {name}");

    internal static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private async Task<bool> IsInScopeAsync(
        EntitySchema entity,
        IReadOnlyDictionary<string, object?> row,
        CallerPrincipal caller,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        var selection = ScopeEvaluator.ResolveEffectiveScope(caller);
        if (selection is null)
        {
            return true;
        }

        var scope = await ResolveScopeAsync(caller, cancellationToken);
        var binding = GetScopeBinding(entity);
        if (binding is null)
        {
            return true;
        }

        var (field, target) = binding.Value;
        var value = row.TryGetValue(field, out var candidate) ? candidate : null;
        if (field == "id" && value is null)
        {
            var parent = entity.Name switch
            {
                "workshop" => ("plant", "plant"),
                "line" => ("workshop", "workshop"),
                _ => (null, null)
            };
            if (parent.Item1 is null ||
                parent.Item2 is null ||
                !row.TryGetValue(parent.Item1, out value) ||
                value is null)
            {
                return false;
            }

            field = parent.Item1;
            target = parent.Item2;
        }

        if (value is null || Convert.ToString(value, CultureInfo.InvariantCulture) == "")
        {
            return true;
        }

        var id = Convert.ToInt32(value, CultureInfo.InvariantCulture);
        var visible = scope.Visible;
        if (visible is not null && target is "plant" or "workshop" or "line")
        {
            return target switch
            {
                "plant" => visible.Plants.Contains(id),
                "workshop" => visible.Workshops.Contains(id),
                "line" => visible.Lines.Contains(id),
                _ => true
            };
        }

        var targetEntity = RequireEntity(target);
        var parameters = new DynamicParameters();
        parameters.Add("id", id);
        var conditions = new List<string>();
        AddScopeCondition(targetEntity, scope, conditions, parameters);
        var where = conditions.Count == 0 ? "" : $" AND {string.Join(" AND ", conditions)}";
        var exists = await connection.QuerySingleOrDefaultAsync<int?>(
            new CommandDefinition(
                $"SELECT 1 FROM {Quote(targetEntity.Name)} WHERE {Quote(RequireColumn(targetEntity, "id"))} = @id{where} LIMIT 1",
                parameters,
                cancellationToken: cancellationToken));
        return exists.HasValue;
    }

    private async Task<ResolvedScope> ResolveScopeAsync(
        CallerPrincipal caller,
        CancellationToken cancellationToken)
    {
        var selection = ScopeEvaluator.ResolveEffectiveScope(caller);
        if (selection is null)
        {
            return new ResolvedScope(null, null);
        }

        var connection = _context.Database.GetDbConnection();
        var closeConnection = await OpenConnectionAsync(connection, cancellationToken);
        try
        {
            var workshopRows = await QueryRowsAsync(
                connection,
                "SELECT id, plant FROM \"workshop\"",
                new { },
                cancellationToken);
            var lineRows = await QueryRowsAsync(
                connection,
                "SELECT id, workshop FROM \"line\"",
                new { },
                cancellationToken);
            var workshopPlants = workshopRows.ToDictionary(
                row => ToInt(row["id"]),
                row => ToNullableInt(row["plant"]));
            var lineWorkshops = lineRows.ToDictionary(
                row => ToInt(row["id"]),
                row => ToNullableInt(row["workshop"]));
            var visible = ScopeEvaluator.ResolveVisibleLocations(
                selection,
                new ScopeHierarchy(workshopPlants, lineWorkshops));
            return new ResolvedScope(selection, visible);
        }
        finally
        {
            await CloseConnectionAsync(connection, closeConnection);
        }
    }

    private void AddScopeCondition(
        EntitySchema entity,
        ResolvedScope scope,
        ICollection<string> conditions,
        DynamicParameters parameters)
    {
        var expression = BuildScopeCondition(entity, scope, parameters, new HashSet<string>(StringComparer.Ordinal));
        if (expression is not null)
        {
            conditions.Add(expression);
        }
    }

    private string? BuildScopeCondition(
        EntitySchema entity,
        ResolvedScope scope,
        DynamicParameters parameters,
        ISet<string> visited)
    {
        var visible = scope.Visible;
        if (visible is null || !visited.Add(entity.Name))
        {
            return null;
        }

        if (entity.Name is "plant" or "workshop" or "line")
        {
            var targetIds = entity.Name switch
            {
                "plant" => visible.Plants,
                "workshop" => visible.Workshops,
                _ => visible.Lines
            };
            var parameterName = $"scope_{entity.Name}";
            parameters.Add(parameterName, targetIds.ToArray());
            return targetIds.Count == 0
                ? "1 = 0"
                : $"{Quote(RequireColumn(entity, "id"))} IN @{parameterName}";
        }

        var binding = GetScopeBinding(entity);
        if (binding is null)
        {
            return null;
        }

        var (field, target) = binding.Value;
        var fieldColumn = Quote(RequireColumn(entity, field));
        if (target is "plant" or "workshop" or "line")
        {
            var targetIds = target switch
            {
                "plant" => visible.Plants,
                "workshop" => visible.Workshops,
                _ => visible.Lines
            };
            var parameterName = $"scope_{target}";
            parameters.Add(parameterName, targetIds.ToArray());
            return targetIds.Count == 0
                ? $"{fieldColumn} IS NULL"
                : $"({fieldColumn} IS NULL OR {fieldColumn} IN @{parameterName})";
        }

        var targetSchema = _catalog.Get(target);
        if (targetSchema is null)
        {
            return null;
        }

        var nested = BuildScopeCondition(targetSchema, scope, parameters, visited);
        if (nested is null)
        {
            return null;
        }

        return $"({fieldColumn} IS NULL OR {fieldColumn} IN (SELECT {Quote(RequireColumn(targetSchema, "id"))} FROM {Quote(targetSchema.Name)} WHERE {nested}))";
    }

    private static (string Field, string Target)? GetScopeBinding(EntitySchema entity)
    {
        if (ScopeBindings.TryGetValue(entity.Name, out var explicitBinding))
        {
            return explicitBinding;
        }

        if (entity.Name is "plant" or "workshop" or "line")
        {
            return ("id", entity.Name);
        }

        foreach (var target in ScopeTargets)
        {
            var field = entity.Fields.FirstOrDefault(
                candidate => string.Equals(candidate.Reference, target, StringComparison.Ordinal));
            if (field is not null && target != entity.Name)
            {
                return (field.Name, target);
            }
        }

        return null;
    }

    private void AddSearchCondition(
        EntitySchema entity,
        string? query,
        ICollection<string> conditions,
        DynamicParameters parameters)
    {
        var keyword = query?.Trim();
        if (string.IsNullOrEmpty(keyword))
        {
            return;
        }

        var searchFields = entity.Fields.Where(field => field.Searchable).ToArray();
        if (searchFields.Length == 0)
        {
            searchFields = entity.Fields.Where(field => field.Type == "str").Take(3).ToArray();
        }

        var columns = searchFields
            .Where(field => entity.Table.Columns.Any(column => column.Name == field.Name))
            .Select(field => $"{Quote(field.Name)} {(IsPostgres ? "ILIKE" : "LIKE")} @search")
            .ToArray();
        if (columns.Length > 0)
        {
            parameters.Add("search", $"%{keyword}%");
            conditions.Add($"({string.Join(" OR ", columns)})");
        }
    }

    private void AddFilterConditions(
        EntitySchema entity,
        IReadOnlyDictionary<string, string?>? filters,
        ICollection<string> conditions,
        DynamicParameters parameters)
    {
        if (filters is null)
        {
            return;
        }

        foreach (var (key, rawValue) in filters)
        {
            if (string.IsNullOrEmpty(rawValue))
            {
                continue;
            }

            var name = key;
            var comparison = "=";
            if (name.EndsWith("__from", StringComparison.Ordinal))
            {
                name = name[..^6];
                comparison = ">=";
            }
            else if (name.EndsWith("__to", StringComparison.Ordinal))
            {
                name = name[..^4];
                comparison = "<";
            }

            var field = entity.Fields.FirstOrDefault(candidate => candidate.Name == name);
            if (!entity.Table.Columns.Any(column => column.Name == name))
            {
                continue;
            }

            object? value;
            if (field is null)
            {
                value = rawValue;
            }
            else
            {
                try
                {
                    value = ConvertValue(field, JsonSerializer.SerializeToElement(rawValue));
                }
                catch (FormatException)
                {
                    throw new EntityOperationException(400, $"筛选值不对：{rawValue}");
                }
            }

            var parameterName = $"filter_{conditions.Count}";
            parameters.Add(parameterName, value);
            conditions.Add($"{Quote(name)} {comparison} @{parameterName}");
        }
    }

    private static (string Column, bool Descending) ResolveSort(EntitySchema entity, string? requestedSort)
    {
        var sort = string.IsNullOrWhiteSpace(requestedSort) ? entity.Order : requestedSort;
        var descending = sort.StartsWith("-", StringComparison.Ordinal);
        var fieldName = descending ? sort[1..] : sort;
        if (!entity.Table.Columns.Any(column => column.Name == fieldName))
        {
            return ("id", descending);
        }

        return (fieldName, descending);
    }

    private async Task AttachLabelsAsync(
        DbConnection connection,
        EntitySchema entity,
        IList<Dictionary<string, object?>> rows,
        CancellationToken cancellationToken)
    {
        foreach (var field in entity.Fields.Where(field => field.Type == "ref" && field.Reference is not null))
        {
            var target = _catalog.Get(field.Reference!);
            if (target is null || !entity.Table.Columns.Any(column => column.Name == field.Name))
            {
                continue;
            }

            var ids = rows
                .Where(row => row.TryGetValue(field.Name, out var value) && value is not null)
                .Select(row => row[field.Name]!)
                .Distinct()
                .ToArray();
            if (ids.Length == 0)
            {
                continue;
            }

            var targetRows = await QueryRowsAsync(
                connection,
                $"SELECT * FROM {Quote(target.Name)} WHERE {Quote(RequireColumn(target, "id"))} IN @ids",
                new { ids },
                cancellationToken);
            var labels = targetRows.ToDictionary(
                row => Convert.ToString(row["id"], CultureInfo.InvariantCulture)!,
                FormatLabel,
                StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!row.TryGetValue(field.Name, out var id) || id is null)
                {
                    continue;
                }

                var key = Convert.ToString(id, CultureInfo.InvariantCulture)!;
                row[field.Name + "__label"] = labels.TryGetValue(key, out var label) ? label : $"#{key}";
            }
        }
    }

    private static string FormatLabel(IReadOnlyDictionary<string, object?> row)
    {
        foreach (var (primary, secondary) in new (string, string?)[]
        {
            ("code", "name"),
            ("sn", null),
            ("username", "name"),
            ("title", null),
            ("name", null),
            ("code", null),
            ("ladle", null)
        })
        {
            if (!row.TryGetValue(primary, out var value) || value is null || Convert.ToString(value) is not { Length: > 0 } first)
            {
                continue;
            }

            if (secondary is null || !row.TryGetValue(secondary, out var secondValue) || secondValue is null)
            {
                return first;
            }

            var second = Convert.ToString(secondValue, CultureInfo.InvariantCulture) ?? "";
            if (string.Equals(first, second, StringComparison.Ordinal))
            {
                return first;
            }

            return second.StartsWith(first + " ", StringComparison.Ordinal) ? second : $"{first} {second}";
        }

        return row.TryGetValue("id", out var id) ? $"#{id}" : "#";
    }

    private async Task<List<Dictionary<string, object?>>> QueryRowsAsync(
        DbConnection connection,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.Select(ToDictionary).ToList();
    }

    private async Task<Dictionary<string, object?>?> QuerySingleRowAsync(
        DbConnection connection,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return row is null ? null : ToDictionary(row);
    }

    private static Dictionary<string, object?> ToDictionary(object row) =>
        ((IDictionary<string, object>)row)
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);

    internal static object? ConvertValue(EntityFieldSchema field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        if (text == "" && field.Type is not ("str" or "text"))
        {
            return null;
        }

        return field.Type switch
        {
            "int" or "ref" => checked((int)double.Parse(text!, CultureInfo.InvariantCulture)),
            "float" => double.Parse(text!, CultureInfo.InvariantCulture),
            "bool" => ParseBoolean(value, text),
            "datetime" => ParseDateTime(text),
            "date" => ParseDate(text),
            "json" when value.ValueKind == JsonValueKind.String => ParseJsonText(text),
            "json" => value.GetRawText(),
            _ => text
        };
    }

    private static bool ParseBoolean(JsonElement value, string? text) =>
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : text?.ToLowerInvariant() is "1" or "true" or "yes" or "on" or "是";

    private static DateTime? ParseDateTime(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value)
            ? value
            : null;

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var value)
            ? value
            : null;

    private static string? ParseJsonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetRawText();
    }

    private static int ToInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static int? ToNullableInt(object? value) =>
        value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private bool IsPostgres => _context.Database.IsNpgsql();

    private static async Task<bool> OpenConnectionAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.State == System.Data.ConnectionState.Open)
        {
            return false;
        }

        await connection.OpenAsync(cancellationToken);
        return true;
    }

    private static async Task CloseConnectionAsync(DbConnection connection, bool openedHere)
    {
        if (openedHere)
        {
            await connection.CloseAsync();
        }
    }

    private sealed record ResolvedScope(ScopeSelection? Selection, VisibleLocationIds? Visible);
}
