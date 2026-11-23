using HoneyMOM.Application.Auditing;

namespace HoneyMOM.Api.Middleware;

public sealed class RequestAuditContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault() ?? "";
        var isApiKey = context.Request.Headers.ContainsKey("X-API-Key") ||
            authorization.StartsWith("Bearer hmk_", StringComparison.Ordinal) ||
            authorization.StartsWith("ApiKey ", StringComparison.Ordinal);
        var forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        var ip = string.IsNullOrWhiteSpace(forwardedFor)
            ? context.Connection.RemoteIpAddress?.ToString()
            : forwardedFor.Split(',', 2)[0].Trim();
        if (ip is { Length: > 60 })
        {
            ip = ip[..60];
        }

        using (AuditRequestContext.Push(isApiKey ? "api" : "ui", ip))
        {
            await next(context);
        }
    }
}
