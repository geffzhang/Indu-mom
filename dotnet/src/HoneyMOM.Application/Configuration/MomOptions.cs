namespace HoneyMOM.Application.Configuration;

public sealed record MomOptions(
    string DataDirectory,
    string DatabaseUrl,
    double SqliteTimeoutSeconds,
    int PostgresPoolSize);
