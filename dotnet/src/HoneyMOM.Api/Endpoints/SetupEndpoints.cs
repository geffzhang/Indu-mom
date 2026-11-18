using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Users;

namespace HoneyMOM.Api.Endpoints;

public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
            "/api/setup/admin",
            async (
                HttpContext context,
                IConfiguration configuration,
                SetupAdminService service,
                CancellationToken cancellationToken) =>
            {
                var demo = IsEnabled(configuration["MOM_DEMO"]);
                return Results.Ok(new
                {
                    needed = await service.NeedsAdminAsync(cancellationToken),
                    demo,
                    lang = RequestLanguageMiddleware.GetLanguage(context)
                });
            });

        endpoints.MapPost(
            "/api/setup/admin",
            async (
                SetupAdminRequest request,
                SetupAdminService service,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var result = await service.CreateAsync(
                    request.Username,
                    request.Password,
                    request.Name,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                return result.Succeeded
                    ? Results.Ok(result.Response)
                    : Results.BadRequest(new
                    {
                        detail = RequestLanguageMiddleware.Translate(
                            result.Error!,
                            RequestLanguageMiddleware.GetLanguage(context))
                    });
            });

        return endpoints;
    }

    private static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";

    public sealed record SetupAdminRequest(string? Username, string? Password, string? Name);
}
