using HoneyMOM.Application.Metadata;
using HoneyMOM.Infrastructure.Persistence.Entities;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class DynamicEntityStore
{
    private readonly HoneyMomDbContext _context;
    private readonly IEntitySchemaCatalog _catalog;
    private static readonly HashSet<string> TypedTables = new(StringComparer.Ordinal)
    {
        "sys_user",
        "sys_role",
        "audit_log",
        "setting"
    };

    public DynamicEntityStore(HoneyMomDbContext context, IEntitySchemaCatalog? catalog = null)
    {
        _context = context;
        _catalog = catalog ?? SchemaSnapshotCatalog.Default;
    }

    public async Task<long> InsertAsync(
        string entityName,
        IDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        if (TypedTables.Contains(entityName))
        {
            return await InsertTypedAsync(entityName, values, cancellationToken);
        }

        var entity = GetGenericEntity(entityName);
        var row = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        _context.Set<Dictionary<string, object?>>(entity.Name).Add(row);
        await _context.SaveChangesAsync(cancellationToken);

        return Convert.ToInt64(row["id"], System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
        string entityName,
        long id,
        CancellationToken cancellationToken)
    {
        if (TypedTables.Contains(entityName))
        {
            return await GetTypedAsync(entityName, id, cancellationToken);
        }

        var entity = GetGenericEntity(entityName);
        var row = await _context.Set<Dictionary<string, object?>>(entity.Name)
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => EF.Property<int>(item, "id") == id,
                cancellationToken);
        return row;
    }

    public async Task UpdateAsync(
        string entityName,
        long id,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        if (TypedTables.Contains(entityName))
        {
            await UpdateTypedAsync(entityName, id, values, cancellationToken);
            return;
        }

        var entity = GetGenericEntity(entityName);
        var row = await _context.Set<Dictionary<string, object?>>(entity.Name)
            .SingleOrDefaultAsync(
                item => EF.Property<int>(item, "id") == id,
                cancellationToken);
        if (row is null)
        {
            return;
        }

        foreach (var (key, value) in values)
        {
            row[key] = value;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        string entityName,
        long id,
        CancellationToken cancellationToken)
    {
        if (TypedTables.Contains(entityName))
        {
            await DeleteTypedAsync(entityName, id, cancellationToken);
            return;
        }

        var entity = GetGenericEntity(entityName);
        var row = await _context.Set<Dictionary<string, object?>>(entity.Name)
            .SingleOrDefaultAsync(
                item => EF.Property<int>(item, "id") == id,
                cancellationToken);
        if (row is not null)
        {
            _context.Set<Dictionary<string, object?>>(entity.Name).Remove(row);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    private EntitySchema GetGenericEntity(string entityName)
    {
        var entity = _catalog.Get(entityName)
            ?? throw new KeyNotFoundException($"Unknown entity '{entityName}'.");
        if (TypedTables.Contains(entityName))
        {
            throw new NotSupportedException(
                $"Entity '{entityName}' uses a typed persistence adapter and cannot be accessed as a shared type.");
        }

        return entity;
    }

    private async Task<long> InsertTypedAsync(
        string entityName,
        IDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        switch (entityName)
        {
            case "sys_user":
            {
                var row = new SystemUserRow();
                ApplyValues(row, values);
                _context.SystemUsers.Add(row);
                await _context.SaveChangesAsync(cancellationToken);
                return row.Id;
            }
            case "sys_role":
            {
                var row = new SystemRoleRow();
                ApplyValues(row, values);
                _context.SystemRoles.Add(row);
                await _context.SaveChangesAsync(cancellationToken);
                return row.Id;
            }
            case "setting":
            {
                var row = new SettingRow();
                ApplyValues(row, values);
                _context.Settings.Add(row);
                await _context.SaveChangesAsync(cancellationToken);
                return row.Id;
            }
            case "audit_log":
            {
                var row = new AuditLogRow();
                ApplyValues(row, values);
                _context.AuditLogs.Add(row);
                await _context.SaveChangesAsync(cancellationToken);
                return row.Id;
            }
            default:
                throw new KeyNotFoundException($"Unknown entity '{entityName}'.");
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>?> GetTypedAsync(
        string entityName,
        long id,
        CancellationToken cancellationToken)
    {
        return entityName switch
        {
            "sys_user" => ToDictionary(await _context.SystemUsers.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)),
            "sys_role" => ToDictionary(await _context.SystemRoles.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)),
            "setting" => ToDictionary(await _context.Settings.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)),
            "audit_log" => ToDictionary(await _context.AuditLogs.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == id, cancellationToken)),
            _ => throw new KeyNotFoundException($"Unknown entity '{entityName}'.")
        };
    }

    private async Task UpdateTypedAsync(
        string entityName,
        long id,
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken)
    {
        switch (entityName)
        {
            case "sys_user":
            {
                var row = await _context.SystemUsers.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    ApplyValues(row, values);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            case "sys_role":
            {
                var row = await _context.SystemRoles.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    ApplyValues(row, values);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            case "setting":
            {
                var row = await _context.Settings.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    ApplyValues(row, values);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            case "audit_log":
            {
                var row = await _context.AuditLogs.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    ApplyValues(row, values);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            default:
                throw new KeyNotFoundException($"Unknown entity '{entityName}'.");
        }
    }

    private async Task DeleteTypedAsync(
        string entityName,
        long id,
        CancellationToken cancellationToken)
    {
        switch (entityName)
        {
            case "sys_user":
            {
                var row = await _context.SystemUsers.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    _context.SystemUsers.Remove(row);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            case "sys_role":
            {
                var row = await _context.SystemRoles.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    _context.SystemRoles.Remove(row);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            case "setting":
            {
                var row = await _context.Settings.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    _context.Settings.Remove(row);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            case "audit_log":
            {
                var row = await _context.AuditLogs.SingleOrDefaultAsync(row => row.Id == id, cancellationToken);
                if (row is not null)
                {
                    _context.AuditLogs.Remove(row);
                    await _context.SaveChangesAsync(cancellationToken);
                }

                break;
            }
            default:
                throw new KeyNotFoundException($"Unknown entity '{entityName}'.");
        }
    }

    private static void ApplyValues(SystemUserRow row, IEnumerable<KeyValuePair<string, object?>> values)
    {
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "username": row.Username = AsString(value); break;
                case "name": row.Name = AsString(value); break;
                case "roles": row.Roles = AsString(value); break;
                case "employee": row.Employee = AsNullableInt(value); break;
                case "active": row.Active = AsNullableBool(value); break;
                case "password_hash": row.PasswordHash = AsString(value); break;
                case "scope": row.Scope = AsString(value); break;
                case "created_at": row.CreatedAt = AsNullableDateTime(value); break;
                case "updated_at": row.UpdatedAt = AsNullableDateTime(value); break;
            }
        }
    }

    private static void ApplyValues(SystemRoleRow row, IEnumerable<KeyValuePair<string, object?>> values)
    {
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "code": row.Code = AsString(value); break;
                case "name": row.Name = AsString(value); break;
                case "perms": row.Perms = AsString(value); break;
                case "scope": row.Scope = AsString(value); break;
                case "builtin": row.Builtin = AsNullableBool(value); break;
                case "remark": row.Remark = AsString(value); break;
                case "created_at": row.CreatedAt = AsNullableDateTime(value); break;
                case "updated_at": row.UpdatedAt = AsNullableDateTime(value); break;
            }
        }
    }

    private static void ApplyValues(SettingRow row, IEnumerable<KeyValuePair<string, object?>> values)
    {
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "key": row.Key = AsString(value); break;
                case "value": row.Value = AsString(value); break;
                case "remark": row.Remark = AsString(value); break;
                case "created_at": row.CreatedAt = AsNullableDateTime(value); break;
                case "updated_at": row.UpdatedAt = AsNullableDateTime(value); break;
            }
        }
    }

    private static void ApplyValues(AuditLogRow row, IEnumerable<KeyValuePair<string, object?>> values)
    {
        foreach (var (key, value) in values)
        {
            switch (key)
            {
                case "time": row.Time = AsNullableDateTime(value); break;
                case "user": row.User = AsString(value); break;
                case "kind": row.Kind = AsString(value); break;
                case "target": row.Target = AsString(value); break;
                case "detail": row.Detail = AsString(value); break;
                case "entity": row.Entity = AsString(value); break;
                case "entity_id": row.EntityId = AsNullableInt(value); break;
                case "action": row.Action = AsString(value); break;
                case "diff": row.Diff = AsString(value); break;
                case "source": row.Source = AsString(value); break;
                case "ip": row.Ip = AsString(value); break;
                case "seq": row.Seq = AsNullableInt(value); break;
                case "prev_hash": row.PrevHash = AsString(value); break;
                case "hash": row.Hash = AsString(value); break;
            }
        }
    }

    private static IReadOnlyDictionary<string, object?>? ToDictionary(SystemUserRow? row) =>
        row is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["username"] = row.Username,
            ["name"] = row.Name,
            ["roles"] = row.Roles,
            ["employee"] = row.Employee,
            ["active"] = row.Active,
            ["password_hash"] = row.PasswordHash,
            ["scope"] = row.Scope,
            ["created_at"] = row.CreatedAt,
            ["updated_at"] = row.UpdatedAt
        };

    private static IReadOnlyDictionary<string, object?>? ToDictionary(SystemRoleRow? row) =>
        row is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["code"] = row.Code,
            ["name"] = row.Name,
            ["perms"] = row.Perms,
            ["scope"] = row.Scope,
            ["builtin"] = row.Builtin,
            ["remark"] = row.Remark,
            ["created_at"] = row.CreatedAt,
            ["updated_at"] = row.UpdatedAt
        };

    private static IReadOnlyDictionary<string, object?>? ToDictionary(SettingRow? row) =>
        row is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["key"] = row.Key,
            ["value"] = row.Value,
            ["remark"] = row.Remark,
            ["created_at"] = row.CreatedAt,
            ["updated_at"] = row.UpdatedAt
        };

    private static IReadOnlyDictionary<string, object?>? ToDictionary(AuditLogRow? row) =>
        row is null ? null : new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = row.Id,
            ["time"] = row.Time,
            ["user"] = row.User,
            ["kind"] = row.Kind,
            ["target"] = row.Target,
            ["detail"] = row.Detail,
            ["entity"] = row.Entity,
            ["entity_id"] = row.EntityId,
            ["action"] = row.Action,
            ["diff"] = row.Diff,
            ["source"] = row.Source,
            ["ip"] = row.Ip,
            ["seq"] = row.Seq,
            ["prev_hash"] = row.PrevHash,
            ["hash"] = row.Hash
        };

    private static string? AsString(object? value) =>
        value is null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static int? AsNullableInt(object? value) =>
        value is null ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool? AsNullableBool(object? value) =>
        value is null ? null : Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture);

    private static DateTime? AsNullableDateTime(object? value) =>
        value is null ? null : value is DateTime dateTime
            ? dateTime
            : DateTime.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
                System.Globalization.CultureInfo.InvariantCulture);
}
