using System.Globalization;
using HoneyMOM.Application.Configuration;

namespace HoneyMOM.Infrastructure.Configuration;

public static class MomOptionsResolver
{
    public static MomOptions Resolve(
        IReadOnlyDictionary<string, string?> values,
        string repositoryRoot)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var configuredDataDirectory = GetValue(
            values,
            "MOM_DATA",
            "HM_DATA",
            Path.Combine(root, "data"));
        var dataDirectory = Path.GetFullPath(configuredDataDirectory);
        var defaultDatabasePath = Path.Combine(dataDirectory, "mes.db").Replace('\\', '/');
        var databaseUrl = GetValue(
            values,
            "MOM_DB",
            "MES_DB",
            $"sqlite:///{defaultDatabasePath}");
        var sqliteTimeout = double.Parse(
            GetValue(values, "MOM_SQLITE_TIMEOUT", "MES_SQLITE_TIMEOUT", "30"),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
        var postgresPoolSize = int.Parse(
            GetValue(values, "MOM_DB_POOL", "MES_DB_POOL", "10"),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture);

        return new MomOptions(
            dataDirectory,
            databaseUrl,
            sqliteTimeout,
            postgresPoolSize);
    }

    private static string GetValue(
        IReadOnlyDictionary<string, string?> values,
        string currentName,
        string legacyName,
        string defaultValue)
    {
        if (values.TryGetValue(currentName, out var currentValue) &&
            !string.IsNullOrEmpty(currentValue))
        {
            return currentValue;
        }

        if (values.TryGetValue(legacyName, out var legacyValue) &&
            !string.IsNullOrEmpty(legacyValue))
        {
            return legacyValue;
        }

        return defaultValue;
    }
}
