using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Plugin.DatabaseOptimize.Services;

/// <summary>
/// Read-only queries on the connection of a <see cref="JellyfinDbContext"/>. The connection must
/// already be open (opened with OpenConnectionAsync).
/// </summary>
internal static class SqliteQuery
{
    /// <summary>
    /// Runs a query and maps every row.
    /// </summary>
    /// <typeparam name="T">The type of the mapped row.</typeparam>
    /// <param name="context">The database context.</param>
    /// <param name="sql">The query.</param>
    /// <param name="map">Maps the current row of the reader.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mapped rows.</returns>
    public static async Task<List<T>> QueryAsync<T>(
        JellyfinDbContext context,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var results = new List<T>();
        var command = context.Database.GetDbConnection().CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.CommandText = sql;
            command.CommandTimeout = 600;
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    results.Add(map(reader));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Runs a query that returns a single number.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="sql">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first value of the first row, or zero.</returns>
    public static async Task<long> ScalarAsync(JellyfinDbContext context, string sql, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            context,
            sql,
            reader => reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? 0L : rows[0];
    }

    /// <summary>
    /// Reads a column as text, returning an empty string for NULL.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="index">The column index.</param>
    /// <returns>The text.</returns>
    public static string Text(DbDataReader reader, int index)
        => reader.IsDBNull(index)
            ? string.Empty
            : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Reads a column as a number, returning zero for NULL.
    /// </summary>
    /// <param name="reader">The reader.</param>
    /// <param name="index">The column index.</param>
    /// <returns>The number.</returns>
    public static long Number(DbDataReader reader, int index)
        => reader.IsDBNull(index) ? 0L : Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture);
}
