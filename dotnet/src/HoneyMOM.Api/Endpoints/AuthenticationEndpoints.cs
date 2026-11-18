using HoneyMOM.Api.Middleware;
using HoneyMOM.Application.Users;

namespace HoneyMOM.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
            "/api/login",
            async (
                LoginRequest request,
                LoginService service,
                HttpContext context,
                CancellationToken cancellationToken) =>
            {
                var result = await service.LoginAsync(
                    request.Username,
                    request.Password,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                return result.Succeeded
                    ? Results.Ok(result.Response)
                    : Results.Json(
                        new
                        {
                            detail = RequestLanguageMiddleware.Translate(
                                result.Error!,
                                RequestLanguageMiddleware.GetLanguage(context))
                        },
                        statusCode: StatusCodes.Status401Unauthorized);
            });

        return endpoints;
    }

    public sealed record LoginRequest(string? Username, string? Password);
}
