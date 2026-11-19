using System.Text.RegularExpressions;
using System.Text;

namespace HoneyMOM.Application.Security;

public static class PermissionEvaluator
{
    public static bool Allows(CallerPrincipal caller, string permission)
    {
        foreach (var role in caller.Roles)
        {
            if (Grants(role.PermissionPatterns, permission))
            {
                return true;
            }
        }

        return caller.AdditionalPermissionPatterns is { } additional &&
            Grants(additional, permission);
    }

    private static bool Grants(IReadOnlyList<string> patterns, string permission)
    {
        var allowed = patterns.Any(pattern =>
            !string.IsNullOrEmpty(pattern) &&
            pattern[0] != '!' &&
            Matches(pattern, permission));
        if (!allowed)
        {
            return false;
        }

        return !patterns.Any(pattern =>
            pattern.Length > 1 &&
            pattern[0] == '!' &&
            Matches(pattern[1..], permission));
    }

    private static bool Matches(string pattern, string value)
    {
        var expression = new StringBuilder("^");
        foreach (var character in pattern)
        {
            expression.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString())
            });
        }

        expression.Append('$');
        return Regex.IsMatch(value, expression.ToString(), RegexOptions.CultureInvariant);
    }
}
