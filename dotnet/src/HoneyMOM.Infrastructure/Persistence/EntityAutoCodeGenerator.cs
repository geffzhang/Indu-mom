using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using HoneyMOM.Application.Entities;
using HoneyMOM.Application.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HoneyMOM.Infrastructure.Persistence;

internal static class EntityAutoCodeGenerator
{
    private static readonly SemaphoreSlim AutoCodeLock = new(1, 1);
    private static readonly Dictionary<(string Entity, string Prefix), int> IssuedCodes = [];

    internal static bool IsMissing(string fieldName, IDictionary<string, object?> values) =>
        !values.TryGetValue(fieldName, out var value) ||
        string.IsNullOrEmpty(Convert.ToString(value, CultureInfo.InvariantCulture));

    internal static async Task<AutoCodeLockLease> AcquireLockAsync(CancellationToken cancellationToken)
    {
        await AutoCodeLock.WaitAsync(cancellationToken);
        return new AutoCodeLockLease();
    }

    internal static async Task GenerateAsync(
        HoneyMomDbContext context,
        EntitySchema entity,
        IDictionary<string, object?> values,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var codeField = FindCodeField(entity, values);
        if (codeField is null)
        {
            return;
        }

        using var autoCodeLock = await AcquireLockAsync(cancellationToken);
        await GenerateCodeAsync(context, entity, codeField, values, now, cancellationToken);
    }

    internal static Task GenerateWithLockHeldAsync(
        HoneyMomDbContext context,
        EntitySchema entity,
        IDictionary<string, object?> values,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var codeField = FindCodeField(entity, values);
        return codeField is null
            ? Task.CompletedTask
            : GenerateCodeAsync(context, entity, codeField, values, now, cancellationToken);
    }

    private static EntityFieldSchema? FindCodeField(
        EntitySchema entity,
        IDictionary<string, object?> values) =>
        entity.Fields.FirstOrDefault(field =>
            !string.IsNullOrEmpty(field.Auto) &&
            IsMissing(field.Name, values));

    private static async Task GenerateCodeAsync(
        HoneyMomDbContext context,
        EntitySchema entity,
        EntityFieldSchema codeField,
        IDictionary<string, object?> values,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var sequenceToken = Regex.Match(codeField.Auto!, @"\{n(?::(\d+))?\}");
        if (!sequenceToken.Success)
        {
            throw new InvalidDataException($"Invalid auto-code pattern for '{entity.Name}.{codeField.Name}'.");
        }

        var date = now.ToString("yyMMdd", CultureInfo.InvariantCulture);
        var pattern = codeField.Auto!;
        var prefixTemplate = pattern[..sequenceToken.Index];
        var suffixTemplate = pattern[(sequenceToken.Index + sequenceToken.Length)..];
        var prefix = prefixTemplate.Replace("{d}", date, StringComparison.Ordinal);
        var suffix = suffixTemplate.Replace("{d}", date, StringComparison.Ordinal);
        var width = sequenceToken.Groups[1].Success
            ? int.Parse(sequenceToken.Groups[1].Value, CultureInfo.InvariantCulture)
            : 0;
        var codeColumn = EntityQueryService.Quote(EntityQueryService.RequireColumn(entity, codeField.Name));
        var table = EntityQueryService.Quote(entity.Name);
        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            var transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            if (context.Database.IsNpgsql())
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT pg_advisory_xact_lock(hashtext(@key))",
                    new { key = $"code:{entity.Name}:{prefix}" },
                    transaction,
                    cancellationToken: cancellationToken));
            }

            var matchingCodes = await connection.QueryAsync<string>(
                new CommandDefinition(
                    $"SELECT {codeColumn} FROM {table} WHERE {codeColumn} LIKE @pattern ESCAPE '\\'",
                    new { pattern = EscapeLike(prefix) + "%" },
                    transaction,
                    cancellationToken: cancellationToken));
            var matcher = new Regex(
                $"^{Regex.Escape(prefix)}(?<n>\\d+){Regex.Escape(suffix)}$",
                RegexOptions.CultureInvariant);
            var maximum = matchingCodes
                .Select(code => matcher.Match(code ?? ""))
                .Where(match => match.Success)
                .Select(match => int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            var key = (entity.Name, prefix + "\0" + suffix);
            var sequence = Math.Max(maximum + 1, IssuedCodes.GetValueOrDefault(key) + 1);
            for (var attempt = 0; attempt < 1000; attempt++, sequence++)
            {
                var digits = width > 0
                    ? sequence.ToString($"D{width}", CultureInfo.InvariantCulture)
                    : sequence.ToString(CultureInfo.InvariantCulture);
                var code = $"{prefix}{digits}{suffix}";
                var exists = await connection.ExecuteScalarAsync<int?>(
                    new CommandDefinition(
                        $"SELECT 1 FROM {table} WHERE {codeColumn} = @code LIMIT 1",
                        new { code },
                        transaction,
                        cancellationToken: cancellationToken));
                if (exists is null)
                {
                    values[codeField.Name] = code;
                    IssuedCodes[key] = sequence;
                    return;
                }
            }

            throw new EntityOperationException(400, "无法生成唯一编码");
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    internal sealed class AutoCodeLockLease : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AutoCodeLock.Release();
            }
        }
    }
}
