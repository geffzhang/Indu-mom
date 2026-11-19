using System.Text.Json;
using HoneyMOM.Application.Security;
using HoneyMOM.Application.Users;
using HoneyMOM.Application.Abstractions;
using HoneyMOM.Infrastructure.Persistence;
using HoneyMOM.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace HoneyMOM.Infrastructure.Security;

public sealed class EfCallerResolver(
    HoneyMomDbContext dbContext,
    ITokenService tokenService) : ICallerResolver
{
    private static readonly IReadOnlyDictionary<string, (string Name, string[] Permissions)> DefaultRoles =
        new Dictionary<string, (string, string[])>(StringComparer.Ordinal)
        {
            ["admin"] = ("系统管理员", ["*"]),
            ["manager"] = ("厂长/经理", ["*", "!system.role.manage", "!system.apikey.manage", "!system.backup.manage", "!system.ai.manage"]),
            ["planner"] = ("计划员", ["page.*", "*.read", "production.*", "master.*"]),
            ["leader"] = ("班组长", ["page.*", "*.read", "production.*", "equipment.*"]),
            ["operator"] = ("操作工", ["page.*", "*.read", "production.*", "equipment.*"]),
            ["qc"] = ("质检员", ["page.*", "*.read", "quality.*"]),
            ["maint"] = ("设备员", ["page.*", "*.read", "equipment.*"]),
            ["warehouse"] = ("仓管员", ["page.*", "*.read", "warehouse.*"]),
            ["engineer"] = ("工艺工程师", ["page.*", "*.read", "master.*", "machining.*", "foundry.*", "quality.*", "ai.*"])
        };

    public async Task<CallerResolutionResult> ResolveAsync(
        CallerCredentials credentials,
        CancellationToken cancellationToken)
    {
        var (token, apiKey) = ExtractCredentials(credentials);
        if (apiKey is not null)
        {
            return CallerResolutionResult.Failed(CallerResolutionFailure.InvalidApiKey);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return CallerResolutionResult.Failed(CallerResolutionFailure.Unauthenticated);
        }

        var username = tokenService.ReadUsername(token, DateTimeOffset.UtcNow);
        if (username is null)
        {
            return CallerResolutionResult.Failed(CallerResolutionFailure.Unauthenticated);
        }

        var user = await dbContext.SystemUsers
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Username == username, cancellationToken);
        if (user is null || user.Active != true)
        {
            return CallerResolutionResult.Failed(CallerResolutionFailure.InactiveUser);
        }

        var roleCodes = (user.Roles ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = await dbContext.SystemRoles
            .AsNoTracking()
            .Where(role => roleCodes.Contains(role.Code!))
            .ToListAsync(cancellationToken);
        var roles = rows.Count == 0
            ? roleCodes
                .Where(DefaultRoles.ContainsKey)
                .Select(code => new CallerRole(code, DefaultRoles[code].Permissions, null))
                .ToArray()
            : rows.Select(ToCallerRole).ToArray();

        return CallerResolutionResult.Success(new CallerPrincipal(
            user.Id,
            user.Username ?? "",
            user.Name,
            roles,
            ParseScope(user.Scope)));
    }

    private static (string? Token, string? ApiKey) ExtractCredentials(CallerCredentials credentials)
    {
        if (!string.IsNullOrWhiteSpace(credentials.ApiKeyHeader))
        {
            return (null, credentials.ApiKeyHeader.Trim());
        }

        var authorization = credentials.AuthorizationHeader ?? "";
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var value = authorization["Bearer ".Length..].Trim();
            return value.StartsWith("hmk_", StringComparison.Ordinal)
                ? (null, value)
                : (value, null);
        }

        if (authorization.StartsWith("ApiKey ", StringComparison.Ordinal))
        {
            var value = authorization["ApiKey ".Length..].Trim();
            if (value.StartsWith("hmk_", StringComparison.Ordinal))
            {
                return (null, value);
            }
        }

        return (
            FirstNonEmpty(credentials.QueryToken, credentials.CookieToken),
            null);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static CallerRole ToCallerRole(SystemRoleRow row) =>
        new(
            row.Code ?? "",
            ParseStringArray(row.Perms),
            ParseScope(row.Scope));

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray()
            : [];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<int>>? ParseScope(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var scope = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
        foreach (var key in new[] { "plants", "workshops", "lines" })
        {
            if (!document.RootElement.TryGetProperty(key, out var values) ||
                values.ValueKind != JsonValueKind.Array)
            {
                scope[key] = [];
                continue;
            }

            scope[key] = values.EnumerateArray()
                .Select(ParseScopeId)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .Distinct()
                .Order()
                .ToArray();
        }

        return scope;
    }

    private static int? ParseScopeId(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        return null;
    }
}
