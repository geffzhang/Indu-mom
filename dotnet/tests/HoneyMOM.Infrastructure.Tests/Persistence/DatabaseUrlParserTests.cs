using HoneyMOM.Infrastructure.Persistence;
using Xunit;

namespace HoneyMOM.Infrastructure.Tests.Persistence;

public sealed class DatabaseUrlParserTests
{
    [Fact]
    public void ParsesWindowsSqliteUrl()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "HoneyMOM", "data");
        var options = DatabaseUrlParser.Parse(
            "sqlite:///C:/Factory%20Data/mes.db",
            dataDirectory);

        Assert.Equal(DatabaseProvider.Sqlite, options.Provider);
        Assert.Equal(
            "Data Source=C:/Factory Data/mes.db",
            options.ConnectionString);
        Assert.Equal(30d, options.SqliteTimeoutSeconds);
        Assert.Equal(10, options.PostgresPoolSize);
        Assert.Equal(Path.GetFullPath(dataDirectory), options.DataDirectory);
    }

    [Fact]
    public void ParsesPostgresSqlalchemyUrlAndEscapesCredentials()
    {
        var options = DatabaseUrlParser.Parse(
            "postgresql+psycopg://work%40example.com:p%40ss%3Aword@db.example:5432/honeymom",
            Path.Combine(Path.GetTempPath(), "HoneyMOM", "data"));

        Assert.Equal(DatabaseProvider.PostgreSql, options.Provider);
        Assert.Equal(
            "Host=db.example;Port=5432;Database=honeymom;Username=work@example.com;Password=p@ss:word",
            options.ConnectionString);
        Assert.Equal(30d, options.SqliteTimeoutSeconds);
        Assert.Equal(10, options.PostgresPoolSize);
        Assert.DoesNotContain("%40", options.ConnectionString);
    }
}
