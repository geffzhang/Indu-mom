using HoneyMOM.Application.Persistence;
using HoneyMOM.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HoneyMOM.Api.Commands;

public static class DatabaseCommands
{
    public static async Task<int> RunAsync(
        string command,
        DatabaseConnectionOptions connectionOptions,
        CancellationToken cancellationToken)
    {
        if (command is not ("check-core" or "baseline"))
        {
            Console.Error.WriteLine($"Unsupported database command: {command}");
            return 2;
        }

        EnsureSqliteDirectoryExists(connectionOptions);
        await using var provider = new ServiceCollection()
            .AddHoneyMomPersistence(connectionOptions)
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<HoneyMomDbContext>();
        var connection = context.Database.GetDbConnection();
        var result = await new CoreSchemaChecker().CheckAsync(connection, cancellationToken);

        if (!result.IsValid)
        {
            foreach (var problem in result.Problems)
            {
                Console.Error.WriteLine(problem);
            }
            return 1;
        }

        if (command == "baseline")
        {
            await context.Database.MigrateAsync(cancellationToken);
            Console.WriteLine("EF Core baseline applied.");
        }
        else
        {
            Console.WriteLine("Core schema OK.");
        }

        return 0;
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
}
