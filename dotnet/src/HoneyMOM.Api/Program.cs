using System.Collections;
using HoneyMOM.Api.Endpoints;
using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Application.Labeling;
using HoneyMOM.Application.Users;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Security;
using HoneyMOM.Api.Commands;
using HoneyMOM.Infrastructure.Labeling;
using HoneyMOM.Infrastructure.Configuration;
using HoneyMOM.Application.Security;
using HoneyMOM.Application.Entities;
using Microsoft.Extensions.DependencyInjection;

if (args.Length > 0 && args[0] == "db")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: db check-core|baseline");
        return 2;
    }

    var momOptions = MomOptionsResolver.Resolve(GetEnvironmentValues(), Directory.GetCurrentDirectory());
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

var runtimeOptions = MomOptionsResolver.Resolve(GetEnvironmentValues(), Directory.GetCurrentDirectory());
var runtimeDatabaseOptions = DatabaseUrlParser.Parse(
    runtimeOptions.DatabaseUrl,
    runtimeOptions.DataDirectory) with
{
    SqliteTimeoutSeconds = runtimeOptions.SqliteTimeoutSeconds,
    PostgresPoolSize = runtimeOptions.PostgresPoolSize
};
Directory.CreateDirectory(runtimeOptions.DataDirectory);

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHoneyMomPersistence(runtimeDatabaseOptions);
builder.Services.AddSingleton(runtimeOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IWorkOrderReleaseLabelService, WorkOrderReleaseLabelService>();
builder.Services.AddScoped<ILabelJobProcessor, LabelJobProcessor>();
builder.Services.AddSingleton<IPrintDeviceTransport, TcpPrintDeviceTransport>();
builder.Services.AddHostedService<LabelQueueWorker>();
builder.Services.AddScoped<IUserAuthenticationStore, EfUserAuthenticationStore>();
builder.Services.AddScoped<ICallerResolver, EfCallerResolver>();
builder.Services.AddSingleton<IPasswordHasher, PythonCompatiblePasswordHasher>();
builder.Services.AddSingleton<ITokenService, PythonCompatibleTokenService>();
builder.Services.AddScoped<SetupAdminService>();
builder.Services.AddScoped<LoginService>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<ApiKeyService>();
builder.Services.AddScoped<EntityQueryService>();
builder.Services.AddScoped<EntityCommandService>();
builder.Services.AddScoped<EntityActionDispatcher>();
builder.Services.AddScoped<IEntityActionHandler, SystemUserResetPasswordHandler>();
var app = builder.Build();

app.UseMiddleware<RequestLanguageMiddleware>();
app.UseMiddleware<RequestAuditContextMiddleware>();
app.UseMiddleware<ApiErrorMiddleware>();

app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapSetupEndpoints();
app.MapAuthenticationEndpoints();
app.MapEntityEndpoints();
app.MapPlatformEndpoints();
app.MapAdminIdentityEndpoints();
app.MapAdminApiKeyEndpoints();
app.MapAdminAuditEndpoints();

await app.RunAsync();
return 0;

static Dictionary<string, string?> GetEnvironmentValues()
{
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
    {
        values[(string)variable.Key] = variable.Value?.ToString();
    }

    return values;
}

public partial class Program { }
