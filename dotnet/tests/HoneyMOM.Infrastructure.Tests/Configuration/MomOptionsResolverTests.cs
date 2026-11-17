using HoneyMOM.Infrastructure.Configuration;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Configuration;

public sealed class MomOptionsResolverTests
{
    private static readonly string RepositoryRoot = Path.Combine(
        Path.GetTempPath(),
        "HoneyMOM");

    [Fact]
    public void CurrentNameWinsOverLegacy()
    {
        var values = new Dictionary<string, string?>
        {
            ["MOM_DATA"] = "new-data",
            ["HM_DATA"] = "old-data",
            ["MOM_DB"] = "sqlite:///new.db",
            ["MES_DB"] = "sqlite:///old.db",
            ["MOM_SQLITE_TIMEOUT"] = "7",
            ["MES_SQLITE_TIMEOUT"] = "4",
            ["MOM_DB_POOL"] = "20",
            ["MES_DB_POOL"] = "10"
        };

        var options = MomOptionsResolver.Resolve(values, RepositoryRoot);

        Assert.Equal(Path.GetFullPath("new-data"), options.DataDirectory);
        Assert.Equal("sqlite:///new.db", options.DatabaseUrl);
        Assert.Equal(7, options.SqliteTimeoutSeconds);
        Assert.Equal(20, options.PostgresPoolSize);
    }

    [Fact]
    public void EmptyCurrentNameFallsBackToLegacy()
    {
        var values = new Dictionary<string, string?>
        {
            ["MOM_DATA"] = "",
            ["HM_DATA"] = "legacy-data",
            ["MOM_DB"] = null,
            ["MES_DB"] = "sqlite:///legacy.db",
            ["MOM_SQLITE_TIMEOUT"] = "",
            ["MES_SQLITE_TIMEOUT"] = "13",
            ["MOM_DB_POOL"] = "",
            ["MES_DB_POOL"] = "22"
        };

        var options = MomOptionsResolver.Resolve(values, RepositoryRoot);

        Assert.Equal(Path.GetFullPath("legacy-data"), options.DataDirectory);
        Assert.Equal("sqlite:///legacy.db", options.DatabaseUrl);
        Assert.Equal(13, options.SqliteTimeoutSeconds);
        Assert.Equal(22, options.PostgresPoolSize);
    }

    [Fact]
    public void DefaultsToRepositoryDataDirectory()
    {
        var options = MomOptionsResolver.Resolve(
            new Dictionary<string, string?>(),
            RepositoryRoot);

        var expectedDataDirectory = Path.GetFullPath(Path.Combine(RepositoryRoot, "data"));
        var expectedDatabasePath = Path.Combine(expectedDataDirectory, "mes.db")
            .Replace('\\', '/');
        Assert.Equal(expectedDataDirectory, options.DataDirectory);
        Assert.Equal($"sqlite:///{expectedDatabasePath}", options.DatabaseUrl);
        Assert.Equal(30, options.SqliteTimeoutSeconds);
        Assert.Equal(10, options.PostgresPoolSize);
    }
}
