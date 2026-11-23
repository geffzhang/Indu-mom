using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using Dapper;
using HoneyMOM.Application.Auditing;
using HoneyMOM.Application.Entities;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Application.Security;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class EntityCommandService
{
    private static readonly HashSet<string> SystemFields = new(StringComparer.Ordinal)
    {
        "id",
        "created_at",
        "updated_at"
    };
    private static readonly SemaphoreSlim AutoCodeLock = new(1, 1);
    private static readonly Dictionary<(string Entity, string Prefix), int> IssuedCodes = [];

    private readonly HoneyMomDbContext _context;
    private readonly IEntitySchemaCatalog _catalog;
    private readonly DynamicEntityStore _store;
    private readonly EntityQueryService _queryService;
    private readonly AuditService _auditService;

    public EntityCommandService(
        HoneyMomDbContext context,
        IEntitySchemaCatalog catalog,
        DynamicEntityStore store,
        EntityQueryService queryService,
        AuditService auditService)
    {
        _context = context;
        _catalog = catalog;
        _store = store;
        _queryService = queryService;
        _auditService = auditService;
    }

    public async Task<long> CreateAsync(
        string entityName,
        IReadOnlyDictionary<string, JsonElement> body,
        CallerPrincipal caller,
        CancellationToken cancellationToken)
    {
        var entity = RequireWritableEntity(entityName);
        var values = ConvertBody(entity, body, isUpdate: false);
        ValidateRequiredFields(entity, values);
        if (!await _queryService.IsInScopeAsync(entityName, values, caller, cancellationToken))
        {
            throw new EntityOperationException(403, "不在你的数据范围内");
        }

        var now = DateTime.Now;
        if (entity.Table.Columns.Any(column => column.Name == "created_at"))
        {
            values["created_at"] = now;
        }

        if (entity.Table.Columns.Any(column => column.Name == "updated_at"))
        {
            values["updated_at"] = now;
        }

        var hasAutoCode = entity.Fields.Any(field =>
            !string.IsNullOrEmpty(field.Auto) &&
            IsAutoCodeMissing(field.Name, values));
        if (hasAutoCode)
        {
            await AutoCodeLock.WaitAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                if (hasAutoCode)
                {
                    await GenerateAutoCodesAsync(entity, values, cancellationToken);
                }

                var id = await _store.InsertAsync(entityName, values, cancellationToken);
                await _auditService.AppendAsync(
                    _context,
                    new AuditEntry(
                        "create",
                        entityName,
                        checked((int)id),
                        BuildDiff(null, values),
                        User: caller.Username,
                        Source: "ui"),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return id;
            }
            catch (Exception exception) when (IsUniqueConstraint(exception))
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new EntityOperationException(400, "编码重复");
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        finally
        {
            if (hasAutoCode)
            {
                AutoCodeLock.Release();
            }
        }
    }

    public async Task<EntityOperationResult> UpdateAsync(
        string entityName,
        long id,
        IReadOnlyDictionary<string, JsonElement> body,
        CallerPrincipal caller,
        CancellationToken cancellationToken)
    {
        var entity = RequireEntity(entityName);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var row = await _store.GetAsync(entityName, id, cancellationToken);
            if (row is null)
            {
                return new EntityOperationResult(false, ErrorCode: "not_found", ErrorMessage: "记录不存在");
            }

            var before = CopyRow(row);
            if (!await _queryService.IsInScopeAsync(entityName, before, caller, cancellationToken))
            {
                throw new EntityOperationException(403, "不在你的数据范围内");
            }

            var values = ConvertBody(entity, body, isUpdate: true);
            if (entity.Table.Columns.Any(column => column.Name == "updated_at"))
            {
                values["updated_at"] = DateTime.Now;
            }

            var after = new Dictionary<string, object?>(before, StringComparer.Ordinal);
            foreach (var (key, value) in values)
            {
                after[key] = value;
            }

            if (!await _queryService.IsInScopeAsync(entityName, after, caller, cancellationToken))
            {
                throw new EntityOperationException(403, "不在你的数据范围内");
            }

            await _store.UpdateAsync(entityName, id, values, cancellationToken);
            await _auditService.AppendAsync(
                _context,
                new AuditEntry(
                    "update",
                    entityName,
                    checked((int)id),
                    BuildDiff(before, after),
                    User: caller.Username,
                    Source: "ui"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EntityOperationResult(true);
        }
        catch (Exception exception) when (IsUniqueConstraint(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new EntityOperationException(400, "编码重复");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<EntityOperationResult> DeleteAsync(
        string entityName,
        long id,
        CallerPrincipal caller,
        CancellationToken cancellationToken)
    {
        var entity = RequireEntity(entityName);
        await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var row = await _store.GetAsync(entityName, id, cancellationToken);
            if (row is null)
            {
                return new EntityOperationResult(false, ErrorCode: "not_found", ErrorMessage: "记录不存在");
            }

            var before = CopyRow(row);
            if (!await _queryService.IsInScopeAsync(entityName, before, caller, cancellationToken))
            {
                throw new EntityOperationException(403, "不在你的数据范围内");
            }

            var referenceCount = await CountReferencesAsync(entityName, id, cancellationToken);
            if (referenceCount > 0)
            {
                throw new EntityOperationException(400, $"还有 {referenceCount} 条记录引用它，不能删除");
            }

            await _store.DeleteAsync(entityName, id, cancellationToken);
            await _auditService.AppendAsync(
                _context,
                new AuditEntry(
                    "delete",
                    entityName,
                    checked((int)id),
                    BuildDiff(before, null),
                    User: caller.Username,
                    Source: "ui"),
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new EntityOperationResult(true);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private EntitySchema RequireWritableEntity(string entityName)
    {
        var entity = _catalog.Get(entityName)
            ?? throw new EntityOperationException(404, $"没有实体 {entityName}");
        if (entity.Internal)
        {
            throw new EntityOperationException(400, $"{entity.Label}由系统自动记录，不能手工新增");
        }

        return entity;
    }

    private EntitySchema RequireEntity(string entityName) =>
        _catalog.Get(entityName) ?? throw new EntityOperationException(404, $"没有实体 {entityName}");

    private Dictionary<string, object?> ConvertBody(
        EntitySchema entity,
        IReadOnlyDictionary<string, JsonElement> body,
        bool isUpdate)
    {
        var fields = entity.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, element) in body)
        {
            if (SystemFields.Contains(key) ||
                key.EndsWith("__label", StringComparison.Ordinal) ||
                !fields.TryGetValue(key, out var field) ||
                isUpdate && field.ReadOnly)
            {
                continue;
            }

            try
            {
                values[key] = EntityQueryService.ConvertValue(field, element);
            }
            catch (Exception exception) when (exception is FormatException or JsonException or OverflowException)
            {
                throw new EntityOperationException(400, $"{field.Label} 格式不对：{element}");
            }
        }

        if (!isUpdate)
        {
            foreach (var field in entity.Fields.Where(field => !values.ContainsKey(field.Name) && field.DefaultJson is not null))
            {
                using var defaultValue = JsonDocument.Parse(field.DefaultJson!);
                values[field.Name] = EntityQueryService.ConvertValue(field, defaultValue.RootElement);
            }
        }

        return values;
    }

    private static void ValidateRequiredFields(EntitySchema entity, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var field in entity.Fields)
        {
            if (field.Required &&
                string.IsNullOrEmpty(Convert.ToString(values.GetValueOrDefault(field.Name), System.Globalization.CultureInfo.InvariantCulture)) &&
                string.IsNullOrEmpty(field.Auto))
            {
                throw new EntityOperationException(400, $"请填写{field.Label}");
            }
        }
    }

    private static bool IsAutoCodeMissing(string fieldName, IDictionary<string, object?> values) =>
        !values.TryGetValue(fieldName, out var value) ||
        string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture));

    private async Task<long> CountReferencesAsync(
        string entityName,
        long id,
        CancellationToken cancellationToken)
    {
        var references = _catalog.Entities
            .SelectMany(entity => entity.Fields
                .Where(field => field.Reference == entityName)
                .Select(field => (Entity: entity, Field: field)))
            .Where(item => item.Entity.Table.Columns.Any(column => column.Name == item.Field.Name))
            .ToArray();
        if (references.Length == 0)
        {
            return 0;
        }

        var connection = _context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            long total = 0;
            foreach (var reference in references)
            {
                total += await Dapper.SqlMapper.QuerySingleAsync<long>(
                    connection,
                    $"SELECT COUNT(*) FROM {EntityQueryService.Quote(reference.Entity.Name)} WHERE {EntityQueryService.Quote(reference.Field.Name)} = @id",
                    new { id });
            }

            return total;
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task GenerateAutoCodesAsync(
        EntitySchema entity,
        IDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        var codeField = entity.Fields.FirstOrDefault(field =>
            !string.IsNullOrEmpty(field.Auto) &&
            IsAutoCodeMissing(field.Name, values));
        if (codeField is null)
        {
            return;
        }

        var sequenceToken = Regex.Match(codeField.Auto!, @"\{n(?::(\d+))?\}");
        if (!sequenceToken.Success)
        {
            throw new InvalidDataException($"Invalid auto-code pattern for '{entity.Name}.{codeField.Name}'.");
        }

        var date = DateTime.Now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var pattern = codeField.Auto!;
        var prefixTemplate = pattern[..sequenceToken.Index];
        var suffixTemplate = pattern[(sequenceToken.Index + sequenceToken.Length)..];
        var prefix = prefixTemplate.Replace("{d}", date, StringComparison.Ordinal);
        var suffix = suffixTemplate.Replace("{d}", date, StringComparison.Ordinal);
        var width = sequenceToken.Groups[1].Success
            ? int.Parse(sequenceToken.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
        var codeColumn = EntityQueryService.Quote(EntityQueryService.RequireColumn(entity, codeField.Name));
        var table = EntityQueryService.Quote(entity.Name);
        var connection = _context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var transaction = _context.Database.CurrentTransaction?.GetDbTransaction();
            if (_context.Database.IsNpgsql())
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(hashtext(@key))",
                    new { key = $"code:{entity.Name}:{prefix}" },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            var matchingCodes = await connection.QueryAsync<string>(
                new CommandDefinition(
                    $"SELECT {codeColumn} FROM {table} WHERE {codeColumn} LIKE @pattern ESCAPE '\\'",
                    new { pattern = EscapeLike(prefix) + "%" },
                    transaction,
                    cancellationToken: cancellationToken));
            var matcher = new Regex(
                $"^{Regex.Escape(prefix)}(?<n>\\d+){Regex.Escape(suffix)}$",
                RegexOptions.CultureInvariant);
            var maximum = matchingCodes
                .Select(code => matcher.Match(code ?? ""))
                .Where(match => match.Success)
                .Select(match => int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            var key = (entity.Name, prefix + "\0" + suffix);
            var sequence = Math.Max(maximum + 1, IssuedCodes.GetValueOrDefault(key) + 1);
            for (var attempt = 0; attempt < 1000; attempt++, sequence++)
            {
                var digits = width > 0
                    ? sequence.ToString($"D{width}", CultureInfo.InvariantCulture)
                    : sequence.ToString(CultureInfo.InvariantCulture);
                var code = $"{prefix}{digits}{suffix}";
                var exists = await connection.ExecuteScalarAsync<int?>(
                    new CommandDefinition(
                        $"SELECT 1 FROM {table} WHERE {codeColumn} = @code LIMIT 1",
                        new { code },
                        transaction,
                        cancellationToken: cancellationToken));
                if (exists is null)
                {
                    values[codeField.Name] = code;
                    IssuedCodes[key] = sequence;
                    return;
                }
            }

            throw new EntityOperationException(400, "无法生成唯一编码");
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static Dictionary<string, object?> CopyRow(IReadOnlyDictionary<string, object?> row) =>
        row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private static object? BuildDiff(
        IReadOnlyDictionary<string, object?>? before,
        IReadOnlyDictionary<string, object?>? after)
    {
        var keys = (before?.Keys ?? []).Concat(after?.Keys ?? []).Distinct(StringComparer.Ordinal);
        var diff = new Dictionary<string, object?[]>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (SystemFields.Contains(key) || key.EndsWith("__label", StringComparison.Ordinal))
            {
                continue;
            }

            var oldValue = before?.GetValueOrDefault(key);
            var newValue = after?.GetValueOrDefault(key);
            if (!Equals(oldValue, newValue))
            {
                diff[key] = [oldValue, newValue];
            }
        }

        return diff.Count == 0 ? null : diff;
    }

    private static bool IsUniqueConstraint(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqliteException { SqliteErrorCode: 19 } ||
                current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return true;
            }
        }

        return false;
    }
}
