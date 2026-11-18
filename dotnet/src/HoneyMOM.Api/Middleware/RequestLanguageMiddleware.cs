using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace HoneyMOM.Api.Middleware;

public sealed class RequestLanguageMiddleware(RequestDelegate next, IConfiguration configuration)
{
    private const string LanguageItemKey = "HoneyMOM.RequestLanguage";

    public async Task InvokeAsync(HttpContext context)
    {
        var language = Resolve(context.Request, configuration);
        context.Items[LanguageItemKey] = language;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ContentLanguage = language;
            return Task.CompletedTask;
        });
        await next(context);
    }

    public static string GetLanguage(HttpContext context) =>
        context.Items.TryGetValue(LanguageItemKey, out var language)
            ? (string)language!
            : "en";

    public static string Translate(string message, string language)
    {
        if (language != "en")
        {
            return message;
        }

        return message switch
        {
            "账号或密码不对" => "Incorrect username or password",
            "管理员账号已存在" => "An administrator account already exists",
            "请填写账号" => "Please enter a username",
            "新密码至少 8 位" => "New password must be at least 8 characters",
            _ => message
        };
    }

    private static string Resolve(HttpRequest request, IConfiguration configuration)
    {
        var language = Normalize(request.Headers["X-Lang"].FirstOrDefault());
        if (language is not null)
        {
            return language;
        }

        language = Normalize(request.Query["lang"].FirstOrDefault());
        if (language is not null)
        {
            return language;
        }

        var acceptedLanguage = request.Headers.AcceptLanguage.FirstOrDefault();
        if (acceptedLanguage is not null)
        {
            language = Normalize(acceptedLanguage.Split(',')[0].Split(';')[0]);
            if (language is not null)
            {
                return language;
            }
        }

        var demo = IsEnabled(configuration["MOM_DEMO"]);
        return Normalize(configuration["MOM_LANG"]) ??
            (demo ? Normalize(configuration["MOM_DEMO_LANG"]) : null) ??
            (demo ? "en" : "en");
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized?.StartsWith("zh", StringComparison.Ordinal) == true)
        {
            return "zh-CN";
        }

        return normalized?.StartsWith("en", StringComparison.Ordinal) == true ? "en" : null;
    }

    private static bool IsEnabled(string? value) =>
        value?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
