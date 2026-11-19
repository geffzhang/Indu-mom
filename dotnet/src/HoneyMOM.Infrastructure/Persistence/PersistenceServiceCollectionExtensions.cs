using System.Data.Common;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Metadata;
using HoneyMOM.Infrastructure.Persistence.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HoneyMOM.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddHoneyMomPersistence(
        this IServiceCollection services,
        DatabaseConnectionOptions connectionOptions)
    {
        EnsureSqliteDirectoryExists(connectionOptions);
        services.AddSingleton<IEntitySchemaCatalog>(_ => SchemaSnapshotCatalog.Default);
        services.AddScoped<DynamicEntityStore>();
        services.AddDbContext<HoneyMomDbContext>(builder =>
        {
            if (connectionOptions.Provider == DatabaseProvider.Sqlite)
            {
                var sqliteOptions = new SqliteConnectionStringBuilder(connectionOptions.ConnectionString)
                {
                    DefaultTimeout = checked((int)Math.Ceiling(connectionOptions.SqliteTimeoutSeconds))
                };
                builder.UseSqlite(sqliteOptions.ToString());
                return;
            }

            var postgresOptions = new NpgsqlConnectionStringBuilder(connectionOptions.ConnectionString)
            {
                MaxPoolSize = connectionOptions.PostgresPoolSize
            };
            builder.UseNpgsql(postgresOptions.ConnectionString);
        });
        services.AddScoped<DbConnection>(_ => CreateConnection(connectionOptions));
        services.AddScoped<IReadStore, DapperReadStore>();
        return services;
    }

    private static void EnsureSqliteDirectoryExists(DatabaseConnectionOptions connectionOptions)
    {
        if (connectionOptions.Provider != DatabaseProvider.Sqlite)
        {
            return;
        }

        var builder = new SqliteConnectionStringBuilder(connectionOptions.ConnectionString);
        if (builder.DataSource == ":memory:" ||
            builder.Mode == SqliteOpenMode.Memory ||
            string.IsNullOrEmpty(builder.DataSource))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(builder.DataSource));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static DbConnection CreateConnection(DatabaseConnectionOptions connectionOptions)
    {
        if (connectionOptions.Provider == DatabaseProvider.Sqlite)
        {
            var sqliteOptions = new SqliteConnectionStringBuilder(connectionOptions.ConnectionString)
            {
                DefaultTimeout = checked((int)Math.Ceiling(connectionOptions.SqliteTimeoutSeconds))
            };
            return new SqliteConnection(sqliteOptions.ToString());
        }

        var postgresOptions = new NpgsqlConnectionStringBuilder(connectionOptions.ConnectionString)
        {
            MaxPoolSize = connectionOptions.PostgresPoolSize
        };
        return new NpgsqlConnection(postgresOptions.ConnectionString);
    }
}
