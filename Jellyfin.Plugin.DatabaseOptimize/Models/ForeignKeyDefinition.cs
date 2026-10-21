namespace Jellyfin.Plugin.DatabaseOptimize.Models;

/// <summary>
/// A foreign key read from the live schema of the database.
/// </summary>
public sealed class ForeignKeyDefinition
{
    /// <summary>
    /// Gets the table that holds the foreign key (the child table).
    /// </summary>
    public required string ChildTable { get; init; }

    /// <summary>
    /// Gets the column of the child table.
    /// </summary>
    public required string ChildColumn { get; init; }

    /// <summary>
    /// Gets the referenced table (the parent table).
    /// </summary>
    public required string ParentTable { get; init; }

    /// <summary>
    /// Gets the referenced column, or an empty string when it could not be resolved.
    /// </summary>
    public required string ParentColumn { get; init; }

    /// <summary>
    /// Gets the ON DELETE action (for example CASCADE or NO ACTION).
    /// </summary>
    public required string OnDelete { get; init; }

    /// <summary>
    /// Gets a value indicating whether the child column accepts NULL.
    /// </summary>
    public required bool IsNullable { get; init; }

    /// <summary>
    /// Gets a value indicating whether the plugin can check and repair this foreign key.
    /// Composite keys and keys whose parent column cannot be resolved are only reported.
    /// </summary>
    public required bool IsSupported { get; init; }

    /// <summary>
    /// Gets a short human readable description.
    /// </summary>
    public string Description =>
        $"{ChildTable}.{ChildColumn} -> {ParentTable}.{ParentColumn} (ON DELETE {OnDelete})";
}
