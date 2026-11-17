using System.Collections;
using HoneyMOM.Api.Commands;
using HoneyMOM.Infrastructure.Configuration;
using HoneyMOM.Infrastructure.Persistence;

if (args.Length > 0 && args[0] == "db")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: db check-core|baseline");
        return 2;
    }

    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
    {
        values[(string)variable.Key] = variable.Value?.ToString();
    }
    var momOptions = MomOptionsResolver.Resolve(values, Directory.GetCurrentDirectory());
    var databaseOptions = DatabaseUrlParser.Parse(
        momOptions.DatabaseUrl,
        momOptions.DataDirectory) with
    {
        SqliteTimeoutSeconds = momOptions.SqliteTimeoutSeconds,
        PostgresPoolSize = momOptions.PostgresPoolSize
    };

    return await DatabaseCommands.RunAsync(
        args[1],
        databaseOptions,
        CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Json(new { status = "ok" }));

await app.RunAsync();
return 0;

public partial class Program { }
