using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Persistence;

public sealed class SchemaBaselineTests
{
    [Fact]
    public async Task BaselinePreservesExistingRowsAndPythonHistory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var root = FindRepositoryRoot();
        var dataDirectory = Path.Combine(temporaryDirectory.Path, "reference-data");
        await CreateReferenceDatabaseAsync(root, dataDirectory);

        await using var missingConnection = new SqliteConnection("Data Source=:memory:");
        await missingConnection.OpenAsync();
        await using (var command = missingConnection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE sys_user (id INTEGER PRIMARY KEY)";
            await command.ExecuteNonQueryAsync();
        }

        var checker = new CoreSchemaChecker();
        var missing = await checker.CheckAsync(missingConnection, CancellationToken.None);
        Assert.False(missing.IsValid);
        Assert.Contains(missing.Problems, problem => problem.Contains("sys_user.username", StringComparison.Ordinal));
        Assert.Contains(missing.Problems, problem => problem.Contains("sys_role", StringComparison.Ordinal));

        var databasePath = Path.Combine(dataDirectory, "mes.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await InsertLegacyRowsAsync(connection);

        var valid = await checker.CheckAsync(connection, CancellationToken.None);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Problems));

        var tableNamesBefore = await ReadTableNamesAsync(connection);
        var rowCountsBefore = await ReadRowCountsAsync(connection, tableNamesBefore);
        var pythonHistoryBefore = await ReadPythonMigrationRowsAsync(connection);
        var contextOptions = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var context = new HoneyMomDbContext(contextOptions))
        {
            await context.Database.MigrateAsync();
        }

        var tableNamesAfter = await ReadTableNamesAsync(connection);
        var rowCountsAfter = await ReadRowCountsAsync(
            connection,
            tableNamesAfter
                .Where(name => name is not "__EFMigrationsHistory" and not "__EFMigrationsLock")
                .ToArray());
        var pythonHistoryAfter = await ReadPythonMigrationRowsAsync(connection);

        var newTables = tableNamesAfter.Except(tableNamesBefore, StringComparer.Ordinal).Order();
        Assert.Equal(
            new[] { "__EFMigrationsHistory", "__EFMigrationsLock" },
            newTables);
        Assert.Equal(rowCountsBefore, rowCountsAfter);
        Assert.Equal(pythonHistoryBefore, pythonHistoryAfter);
        Assert.Equal(1, await ReadEfBaselineCountAsync(connection));
    }

    [PostgreSqlFact]
    public async Task BaselinePreservesPostgresRowsAndPythonHistory()
    {
        var url = Environment.GetEnvironmentVariable("FC_PG")!;
        var connectionOptions = DatabaseUrlParser.Parse(url, Path.GetTempPath());
        var parsedUrl = new Uri(url.Replace("postgresql+psycopg://", "postgresql://", StringComparison.Ordinal));
        var adminConnectionString = new NpgsqlConnectionStringBuilder(connectionOptions.ConnectionString)
        {
            Database = string.IsNullOrEmpty(parsedUrl.AbsolutePath.Trim('/'))
                ? "postgres"
                : Uri.UnescapeDataString(parsedUrl.AbsolutePath.Trim('/'))
        };
        var schema = $"hm_schema_test_{Guid.NewGuid():N}";

        await using var adminConnection = new NpgsqlConnection(adminConnectionString.ConnectionString);
        await adminConnection.OpenAsync();
        await using (var createSchema = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", adminConnection))
        {
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            var testConnectionString = new NpgsqlConnectionStringBuilder(adminConnectionString.ConnectionString)
            {
                SearchPath = schema
            };
            await using var connection = new NpgsqlConnection(testConnectionString.ConnectionString);
            await connection.OpenAsync();
            var contextOptions = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseNpgsql(connection)
                .Options;

            await using (var context = new HoneyMomDbContext(contextOptions))
            {
                await context.Database.EnsureCreatedAsync();
                context.SystemUsers.Add(new SystemUserRow
                {
                    Username = "legacy-admin",
                    Name = "Legacy Admin",
                    Roles = "admin",
                    Active = true,
                    PasswordHash = "fixture-hash",
                    Scope = """{"plant_ids":[1]}"""
                });
                context.SystemRoles.Add(new SystemRoleRow
                {
                    Code = "legacy-role",
                    Name = "Legacy Role",
                    Perms = """["user:read"]""",
                    Scope = """{"plant_ids":[1]}""",
                    Builtin = true
                });
                await context.SaveChangesAsync();
            }

            await using (var createPythonHistory = new NpgsqlCommand(
                "CREATE TABLE schema_migration (rev varchar(40) PRIMARY KEY, title varchar(200), " +
                "applied_at timestamp, ms integer, dialect varchar(20), ops integer); " +
                "INSERT INTO schema_migration (rev, title, applied_at, ms, dialect, ops) " +
                "VALUES ('0001', 'python baseline', CURRENT_TIMESTAMP, 1, 'postgresql', 2)",
                connection))
            {
                await createPythonHistory.ExecuteNonQueryAsync();
            }

            var checker = new CoreSchemaChecker();
            var valid = await checker.CheckAsync(connection, CancellationToken.None);
            Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Problems));

            var tableNamesBefore = await ReadPostgresTableNamesAsync(connection);
            var rowCountsBefore = await ReadPostgresRowCountsAsync(connection, tableNamesBefore);
            var pythonHistoryBefore = await ReadPythonMigrationRowsAsync(connection);
            await using (var context = new HoneyMomDbContext(contextOptions))
            {
                await context.Database.MigrateAsync();
            }

            var tableNamesAfter = await ReadPostgresTableNamesAsync(connection);
            var rowCountsAfter = await ReadPostgresRowCountsAsync(
                connection,
                tableNamesAfter.Where(name => name != "__EFMigrationsHistory"));
            var pythonHistoryAfter = await ReadPythonMigrationRowsAsync(connection);

            Assert.Equal(
                new[] { "__EFMigrationsHistory" },
                tableNamesAfter.Except(tableNamesBefore, StringComparer.Ordinal).Order());
            Assert.Equal(rowCountsBefore, rowCountsAfter);
            Assert.Equal(pythonHistoryBefore, pythonHistoryAfter);
            Assert.Equal(1, await ReadEfBaselineCountAsync(connection));
        }
        finally
        {
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateReferenceDatabaseAsync(string repositoryRoot, string dataDirectory)
    {
        var nestedReference = Path.Combine(repositoryRoot, "honeymom-python-reference");
        var reference = Directory.Exists(nestedReference)
            ? nestedReference
            : Path.GetFullPath(Path.Combine(repositoryRoot, "..", "honeymom-python-reference"));
        var startInfo = new ProcessStartInfo("python")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("tests/porting/create_reference_db.py");
        startInfo.ArgumentList.Add("--reference");
        startInfo.ArgumentList.Add(reference);
        startInfo.ArgumentList.Add("--data-dir");
        startInfo.ArgumentList.Add(dataDirectory);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add("production");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the Python reference database fixture.");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Reference database creation failed ({process.ExitCode}).{Environment.NewLine}{stdout}{stderr}");
    }

    private static async Task InsertLegacyRowsAsync(DbConnection connection)
    {
        var commands = new[]
        {
            """
            INSERT INTO sys_user (username, name, roles, active, password_hash, scope)
            VALUES ('legacy-admin', 'Legacy Admin', 'admin', 1, 'fixture-hash', '{"plant_ids":[1]}')
            """,
            """
            INSERT INTO sys_role (code, name, perms, scope, builtin)
            VALUES ('legacy-role', 'Legacy Role', '["user:read"]', '{"plant_ids":[1]}', 1)
            """,
            """
            INSERT INTO audit_log (time, user, kind, target, detail)
            VALUES (CURRENT_TIMESTAMP, 'legacy-admin', 'baseline', 'legacy', '{}')
            """
        };

        foreach (var sql in commands)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string[]> ReadTableNamesAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names.ToArray();
    }

    private static async Task<Dictionary<string, long>> ReadRowCountsAsync(
        DbConnection connection,
        IEnumerable<string> tableNames)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var tableName in tableNames)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            counts.Add(
                tableName,
                Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        return counts;
    }

    private static async Task<string[]> ReadPythonMigrationRowsAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rev, title, applied_at, ms, dialect, ops FROM schema_migration ORDER BY rev";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            var values = new string[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[index] = reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
            }
            rows.Add(string.Join('\u001f', values));
        }
        return rows.ToArray();
    }

    private static async Task<long> ReadEfBaselineCountAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        var migrationIdColumn = connection is NpgsqlConnection
            ? "\"MigrationId\""
            : "MigrationId";
        command.CommandText =
            $"SELECT COUNT(*) FROM \"__EFMigrationsHistory\" WHERE {migrationIdColumn} = " +
            "'20261117000000_BaselinePythonSchema'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string[]> ReadPostgresTableNamesAsync(DbConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }
        return names.ToArray();
    }

    private static async Task<Dictionary<string, long>> ReadPostgresRowCountsAsync(
        DbConnection connection,
        IEnumerable<string> tableNames)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var tableName in tableNames)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\"";
            counts.Add(
                tableName,
                Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        return counts;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "porting", "reference-lock.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate porting/reference-lock.json from the test directory.");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"honeymom-schema-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
