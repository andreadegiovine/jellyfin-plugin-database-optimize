using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.DatabaseOptimize.Models;

namespace Jellyfin.Plugin.DatabaseOptimize.Services;

/// <summary>
/// Reads the live schema (tables, columns and foreign keys) from SQLite, so the checks follow the
/// schema of the running server instead of a hard coded list.
/// </summary>
internal static class DatabaseSchemaReader
{
    private sealed record ColumnRow(string Table, string Name, bool NotNull, long PrimaryKeyPosition);

    private sealed record ForeignKeyRow(string Table, long Id, long Seq, string Parent, string From, string To, string OnDelete);

    /// <summary>
    /// Reads the schema. The connection of <paramref name="context"/> must already be open.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The schema.</returns>
    public static async Task<DatabaseSchema> ReadAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var tables = await SqliteQuery.QueryAsync(
            context,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
            reader => SqliteQuery.Text(reader, 0),
            cancellationToken).ConfigureAwait(false);

        var columnRows = await SqliteQuery.QueryAsync(
            context,
            "SELECT m.name, p.\"name\", p.\"notnull\", p.\"pk\" FROM sqlite_master m, pragma_table_info(m.name) p WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%'",
            reader => new ColumnRow(
                SqliteQuery.Text(reader, 0),
                SqliteQuery.Text(reader, 1),
                SqliteQuery.Number(reader, 2) != 0,
                SqliteQuery.Number(reader, 3)),
            cancellationToken).ConfigureAwait(false);

        var foreignKeyRows = await SqliteQuery.QueryAsync(
            context,
            "SELECT m.name, f.\"id\", f.\"seq\", f.\"table\", f.\"from\", f.\"to\", f.\"on_delete\" FROM sqlite_master m, pragma_foreign_key_list(m.name) f WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%' ORDER BY m.name, f.\"id\", f.\"seq\"",
            reader => new ForeignKeyRow(
                SqliteQuery.Text(reader, 0),
                SqliteQuery.Number(reader, 1),
                SqliteQuery.Number(reader, 2),
                SqliteQuery.Text(reader, 3),
                SqliteQuery.Text(reader, 4),
                SqliteQuery.Text(reader, 5),
                SqliteQuery.Text(reader, 6)),
            cancellationToken).ConfigureAwait(false);

        var columns = columnRows
            .GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)g.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var columnInfo = columnRows.ToDictionary(
            c => (c.Table.ToUpperInvariant(), c.Name.ToUpperInvariant()),
            c => c);

        var primaryKeys = columnRows
            .Where(c => c.PrimaryKeyPosition > 0)
            .GroupBy(c => c.Table, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.PrimaryKeyPosition).Select(c => c.Name).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var foreignKeys = new List<ForeignKeyDefinition>();
        foreach (var group in foreignKeyRows.GroupBy(r => (r.Table, r.Id)))
        {
            var first = group.OrderBy(r => r.Seq).First();
            var isComposite = group.Count() > 1;

            var parentColumn = first.To;
            if (string.IsNullOrEmpty(parentColumn)
                && primaryKeys.TryGetValue(first.Parent, out var parentKey)
                && parentKey.Count == 1)
            {
                parentColumn = parentKey[0];
            }

            var isNullable = !columnInfo.TryGetValue((first.Table.ToUpperInvariant(), first.From.ToUpperInvariant()), out var child)
                             || (!child.NotNull && child.PrimaryKeyPosition == 0);

            var isSupported = !isComposite
                              && !string.IsNullOrEmpty(parentColumn)
                              && SqlText.IsSafeIdentifier(first.Table)
                              && SqlText.IsSafeIdentifier(first.From)
                              && SqlText.IsSafeIdentifier(first.Parent)
                              && SqlText.IsSafeIdentifier(parentColumn);

            foreignKeys.Add(new ForeignKeyDefinition
            {
                ChildTable = first.Table,
                ChildColumn = first.From,
                ParentTable = first.Parent,
                ParentColumn = parentColumn,
                OnDelete = first.OnDelete,
                IsNullable = isNullable,
                IsSupported = isSupported
            });
        }

        return new DatabaseSchema
        {
            Tables = tables,
            ForeignKeys = foreignKeys,
            Columns = columns
        };
    }
}
