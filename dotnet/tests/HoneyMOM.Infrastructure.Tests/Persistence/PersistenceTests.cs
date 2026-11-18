using System.Data.Common;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Persistence;

public sealed class PersistenceTests
{
    [Fact]
    public async Task MappedCoreRowsRoundTripOnBothProviders()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
            .UseSqlite(connection)
            .Options;

        await VerifyRoundTripAsync(connection, options);
    }

    [Fact]
    public async Task PersistenceRegistrationCreatesCustomSqliteDatabaseDirectory()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var dataDirectory = Path.Combine(temporaryDirectory.Path, "data");
        var databasePath = Path.Combine(temporaryDirectory.Path, "custom", "nested", "mes.db");
        Directory.CreateDirectory(dataDirectory);
        var connectionOptions = DatabaseUrlParser.Parse(
            $"sqlite:///{databasePath.Replace('\\', '/')}",
            dataDirectory) with
        {
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString()
        };

        await using var provider = new ServiceCollection()
            .AddHoneyMomPersistence(connectionOptions)
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        await db.Database.EnsureCreatedAsync();

        Assert.True(File.Exists(databasePath));
    }

    [PostgreSqlFact]
    public async Task MappedCoreRowsRoundTripOnPostgreSql()
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
        var schema = $"hm_test_{Guid.NewGuid():N}";

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
            var options = new DbContextOptionsBuilder<HoneyMomDbContext>()
                .UseNpgsql(connection)
                .Options;

            await VerifyRoundTripAsync(connection, options);
        }
        finally
        {
            await using var dropSchema = new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    private static async Task VerifyRoundTripAsync(
        DbConnection connection,
        DbContextOptions<HoneyMomDbContext> contextOptions)
    {
        await using var context = new HoneyMomDbContext(contextOptions);
        await context.Database.EnsureCreatedAsync();

        var user = new SystemUserRow
        {
            Username = "persistence-test",
            Name = "Before",
            Roles = "admin",
            Active = true,
            PasswordHash = "fixture",
            Scope = """{"plant_ids":[1,2]}"""
        };
        context.SystemUsers.Add(user);
        await context.SaveChangesAsync();

        var readStore = new DapperReadStore(connection);
        var userSql =
            "SELECT id AS Id, username AS Username, name AS Name, roles AS Roles, employee AS Employee, " +
            "active AS Active, password_hash AS PasswordHash, scope AS Scope, created_at AS CreatedAt, " +
            "updated_at AS UpdatedAt FROM sys_user WHERE username = @Username";
        var readUser = await readStore.QuerySingleOrDefaultAsync<SystemUserRow>(
            userSql,
            new { user.Username },
            CancellationToken.None);
        Assert.NotNull(readUser);
        Assert.Equal("""{"plant_ids":[1,2]}""", readUser.Scope);

        user.Name = "After";
        await context.SaveChangesAsync();

        var updatedUser = await readStore.QuerySingleOrDefaultAsync<SystemUserRow>(
            userSql,
            new { user.Username },
            CancellationToken.None);
        Assert.Equal("After", updatedUser?.Name);

        var role = new SystemRoleRow
        {
            Code = "persistence-test",
            Name = "Persistence Test",
            Perms = """["user:read","user:write"]""",
            Scope = """{"plant_ids":[1]}"""
        };
        context.SystemRoles.Add(role);
        await context.SaveChangesAsync();

        var readRole = await readStore.QuerySingleOrDefaultAsync<SystemRoleRow>(
            "SELECT id AS Id, code AS Code, name AS Name, perms AS Perms, scope AS Scope, " +
            "builtin AS Builtin, remark AS Remark, created_at AS CreatedAt, updated_at AS UpdatedAt " +
            "FROM sys_role WHERE code = @Code",
            new { role.Code },
            CancellationToken.None);
        Assert.NotNull(readRole);
        Assert.Equal("""["user:read","user:write"]""", readRole.Perms);
        Assert.Equal("""{"plant_ids":[1]}""", readRole.Scope);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"honeymom-persistence-test-{Guid.NewGuid():N}");

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FC_PG")))
        {
            Skip = "FC_PG is not configured";
        }
    }
}
