using System.Data.Common;
using HoneyMOM.Application.Abstractions;
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
