using System.Collections.Generic;

namespace Jellyfin.Plugin.DatabaseOptimize.Models;

/// <summary>
/// The schema of the database as it is right now: tables, columns and foreign keys.
/// </summary>
public sealed class DatabaseSchema
{
    /// <summary>
    /// Gets the names of all the tables.
    /// </summary>
    public required IReadOnlyList<string> Tables { get; init; }

    /// <summary>
    /// Gets all the foreign keys.
    /// </summary>
    public required IReadOnlyList<ForeignKeyDefinition> ForeignKeys { get; init; }

    /// <summary>
    /// Gets the column names of every table, compared case-insensitively.
    /// </summary>
    public required IReadOnlyDictionary<string, IReadOnlySet<string>> Columns { get; init; }

    /// <summary>
    /// Checks whether a table has a column.
    /// </summary>
    /// <param name="table">The table name.</param>
    /// <param name="column">The column name.</param>
    /// <returns><c>true</c> when the column exists.</returns>
    public bool HasColumn(string table, string column)
        => Columns.TryGetValue(table, out var columns) && columns.Contains(column);

    /// <summary>
    /// Checks whether the schema has the tables and columns used by Jellyfin to model alternate
    /// versions of the same video (BaseItems.OwnerId, BaseItems.PrimaryVersionId and
    /// LinkedChildren).
    /// </summary>
    /// <returns><c>true</c> when the alternate versions model is present.</returns>
    public bool HasAlternateVersionModel()
        => HasColumn("BaseItems", "Id")
           && HasColumn("BaseItems", "OwnerId")
           && HasColumn("BaseItems", "PrimaryVersionId")
           && HasColumn("BaseItems", "ExtraType")
           && HasColumn("BaseItems", "Path")
           && HasColumn("LinkedChildren", "ParentId")
           && HasColumn("LinkedChildren", "ChildId")
           && HasColumn("LinkedChildren", "ChildType")
           && HasColumn("LinkedChildren", "SortOrder");
}
