namespace HoneyMOM.Infrastructure.Persistence;

public enum DatabaseProvider
{
    Sqlite,
    PostgreSql
}

public sealed record DatabaseConnectionOptions(
    DatabaseProvider Provider,
    string ConnectionString,
    double SqliteTimeoutSeconds,
    int PostgresPoolSize,
    string DataDirectory);

public static class DatabaseUrlParser
{
    private const double DefaultSqliteTimeoutSeconds = 30;
    private const int DefaultPostgresPoolSize = 10;

    public static DatabaseConnectionOptions Parse(string? url, string dataDirectory)
    {
        var normalizedDataDirectory = Path.GetFullPath(dataDirectory);
        var resolvedUrl = string.IsNullOrEmpty(url)
            ? $"sqlite:///{Path.Combine(normalizedDataDirectory, "mes.db").Replace('\\', '/')}"
            : url;

        if (resolvedUrl.StartsWith("sqlite:///", StringComparison.Ordinal))
        {
            var sqlitePath = Uri.UnescapeDataString(resolvedUrl["sqlite:///".Length..]);
            if (string.IsNullOrEmpty(sqlitePath))
            {
                throw new FormatException("SQLite URL must include a database path.");
            }

            return new DatabaseConnectionOptions(
                DatabaseProvider.Sqlite,
                $"Data Source={QuoteConnectionValue(sqlitePath)}",
                DefaultSqliteTimeoutSeconds,
                DefaultPostgresPoolSize,
                normalizedDataDirectory);
        }

        if (resolvedUrl.StartsWith("postgresql+psycopg://", StringComparison.Ordinal) ||
            resolvedUrl.StartsWith("postgresql://", StringComparison.Ordinal))
        {
            return ParsePostgreSql(resolvedUrl, normalizedDataDirectory);
        }

        throw new NotSupportedException($"Unsupported database URL scheme: {resolvedUrl}");
    }

    private static DatabaseConnectionOptions ParsePostgreSql(
        string url,
        string dataDirectory)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.Host))
        {
            throw new FormatException("PostgreSQL URL must include a valid host.");
        }

        var userInfo = Uri.UnescapeDataString(uri.UserInfo);
        var separator = userInfo.IndexOf(':');
        var username = separator < 0 ? userInfo : userInfo[..separator];
        var password = separator < 0 ? string.Empty : userInfo[(separator + 1)..];
        var database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        var port = uri.IsDefaultPort ? 5432 : uri.Port;
        var connectionParts = new List<string>
        {
            $"Host={QuoteConnectionValue(uri.Host)}",
            $"Port={port}",
            $"Database={QuoteConnectionValue(database)}",
            $"Username={QuoteConnectionValue(username)}",
            $"Password={QuoteConnectionValue(password)}"
        };

        AddQueryOptions(uri.Query, connectionParts);

        return new DatabaseConnectionOptions(
            DatabaseProvider.PostgreSql,
            string.Join(';', connectionParts),
            DefaultSqliteTimeoutSeconds,
            DefaultPostgresPoolSize,
            dataDirectory);
    }

    private static void AddQueryOptions(string query, ICollection<string> connectionParts)
    {
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var optionNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sslmode"] = "SSL Mode",
            ["application_name"] = "Application Name",
            ["connect_timeout"] = "Timeout"
        };

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(separator < 0 ? pair : pair[..separator]);
            var value = Uri.UnescapeDataString(separator < 0 ? string.Empty : pair[(separator + 1)..]);
            var connectionName = optionNames.TryGetValue(name, out var mappedName)
                ? mappedName
                : name;
            connectionParts.Add(
                $"{connectionName}={QuoteConnectionValue(value)}");
        }
    }

    private static string QuoteConnectionValue(string value)
    {
        if (value.IndexOfAny([';', '"']) < 0 &&
            !value.StartsWith(' ') &&
            !value.EndsWith(' '))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
