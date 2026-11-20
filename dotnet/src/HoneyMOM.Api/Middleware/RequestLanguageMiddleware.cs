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
            "原密码不对" => "Incorrect current password",
            "新密码至少 6 位" => "New password must be at least 6 characters",
            "请填写角色名称" => "Enter a role name",
            "角色编码只能用小写字母、数字和下划线" => "Role code can contain only lowercase letters, numbers, and underscores",
            "没有这个角色" => "Role not found",
            "只有内置角色能恢复默认" => "Only built-in roles can be restored to defaults",
            "内置角色不能删除（可以改权限）" => "Built-in roles cannot be deleted (you can change their permissions)",
            "不能去掉自己的管理员角色" => "You cannot remove your own administrator role",
            "系统管理员角色必须保留全部权限（*），否则可能没人能再管理权限" =>
                "The system administrator role must retain all permissions (*), or no one may be able to manage permissions",
            "请填写名称" => "Please enter a name",
            "API 密钥格式不对" => "Invalid API key format",
            "API 密钥无效" => "Invalid API key",
            "API 密钥已吊销" => "API key has been revoked",
            "API 密钥已过期" => "API key has expired",
            "日期时间格式不对" => "Invalid date and time format",
            "密钥只显示这一次，请立即复制保存" => "The key is shown only once. Copy and save it now",
            _ when message.StartsWith("API 密钥不允许从 ", StringComparison.Ordinal) &&
                message.EndsWith(" 调用（IP 白名单）", StringComparison.Ordinal) =>
                $"API key is not allowed to make calls from {message["API 密钥不允许从 ".Length..^" 调用（IP 白名单）".Length]} (IP allowlist)",
            _ when message.StartsWith("IP 白名单格式不对：", StringComparison.Ordinal) =>
                $"Invalid IP allowlist format: {message["IP 白名单格式不对：".Length..]}",
            _ when message.StartsWith("角色 ", StringComparison.Ordinal) && message.EndsWith(" 已存在", StringComparison.Ordinal) =>
                $"Role {message["角色 ".Length..^" 已存在".Length]} already exists",
            _ when message.StartsWith("还有 ", StringComparison.Ordinal) && message.EndsWith(" 个用户用这个角色，先把他们换成别的角色", StringComparison.Ordinal) =>
                $"{message["还有 ".Length..message.IndexOf(" 个用户", StringComparison.Ordinal)]} users still have this role. Assign them a different role first",
            _ when message.StartsWith("没有这些角色：", StringComparison.Ordinal) =>
                $"Roles not found: {message["没有这些角色：".Length..]}",
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
