using System.Data.Common;
using Dapper;
using HoneyMOM.Application.Abstractions;

namespace HoneyMOM.Infrastructure.Persistence;

public sealed class DapperReadStore(DbConnection connection) : IReadStore
{
    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken);
        var rows = await connection.QueryAsync<T>(command);
        return rows.AsList();
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(
        string sql,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await EnsureOpenAsync(cancellationToken);
        var command = new CommandDefinition(
            sql,
            parameters,
            cancellationToken: cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<T>(command);
    }

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
    }
}
