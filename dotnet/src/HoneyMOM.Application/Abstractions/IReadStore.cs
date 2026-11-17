namespace HoneyMOM.Application.Abstractions;

public interface IReadStore
{
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters,
        CancellationToken cancellationToken);

    Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters,
        CancellationToken cancellationToken);
}
