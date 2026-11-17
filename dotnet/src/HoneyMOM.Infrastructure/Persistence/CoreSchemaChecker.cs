using System.Data.Common;
using HoneyMOM.Application.Persistence;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class CoreSchemaChecker
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["sys_user"] =
            [
                "id", "username", "name", "roles", "employee", "active", "password_hash",
                "scope", "created_at", "updated_at"
            ],
            ["sys_role"] =
            [
                "id", "code", "name", "perms", "scope", "builtin", "remark",
                "created_at", "updated_at"
            ],
            ["setting"] =
            [
                "id", "key", "value", "remark", "created_at", "updated_at"
            ],
            ["audit_log"] =
            [
                "id", "time", "user", "kind", "target", "detail", "entity", "entity_id",
                "action", "diff", "source", "ip", "seq", "prev_hash", "hash"
            ]
        };

    public async Task<SchemaCheckResult> CheckAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var problems = new List<string>();
        foreach (var (table, requiredColumns) in RequiredColumns)
        {
            var actualColumns = connection switch
            {
                SqliteConnection sqlite => await ReadSqliteColumnsAsync(
                    sqlite,
                    table,
                    cancellationToken),
                NpgsqlConnection postgres => await ReadPostgresColumnsAsync(
                    postgres,
                    table,
                    cancellationToken),
                _ => throw new NotSupportedException(
                    $"Unsupported connection type: {connection.GetType().FullName}")
            };

            if (actualColumns.Count == 0)
            {
                problems.Add($"Missing table {table}");
                continue;
            }

            foreach (var column in requiredColumns)
            {
                if (!actualColumns.Contains(column))
                {
                    problems.Add($"Missing column {table}.{column}");
                }
            }
        }

        return new SchemaCheckResult(problems);
    }

    private static async Task<HashSet<string>> ReadSqliteColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }
        return columns;
    }

    private static async Task<HashSet<string>> ReadPostgresColumnsAsync(
        NpgsqlConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT column_name FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND table_name = @table";
        command.Parameters.AddWithValue("table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }
}
