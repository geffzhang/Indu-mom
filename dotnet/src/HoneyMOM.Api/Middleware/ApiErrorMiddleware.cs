using System.Text.Json;

namespace HoneyMOM.Api.Middleware;

public sealed class ApiErrorMiddleware(
    RequestDelegate next,
    ILogger<ApiErrorMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            logger.LogError(exception, "Unhandled request failure for {Path}", context.Request.Path);
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { detail = "Internal server error" },
                JsonSerializerOptions.Web,
                context.RequestAborted);
        }
    }
}
