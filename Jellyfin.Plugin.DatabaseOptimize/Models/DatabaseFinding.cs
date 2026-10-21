namespace Jellyfin.Plugin.DatabaseOptimize.Models;

/// <summary>
/// The kind of problem found in the database.
/// </summary>
public enum FindingKind
{
    /// <summary>
    /// Rows that reference a parent row that does not exist (foreign key violation).
    /// </summary>
    OrphanedRows,

    /// <summary>
    /// An alternate version owned by another alternate version instead of the primary one.
    /// </summary>
    AlternateOwnerChain,

    /// <summary>
    /// An alternate version whose PrimaryVersionId differs from its OwnerId.
    /// </summary>
    AlternatePrimaryMismatch,

    /// <summary>
    /// A LinkedChildren alternate version link that does not match BaseItems.OwnerId.
    /// </summary>
    AlternateLinkInconsistent,

    /// <summary>
    /// An alternate version without its LinkedChildren link.
    /// </summary>
    AlternateLinkMissing,

    /// <summary>
    /// An alternate version whose file no longer exists although its folder does.
    /// </summary>
    AlternateFileMissing
}

/// <summary>
/// A problem found by the diagnosis.
/// </summary>
public sealed class DatabaseFinding
{
    /// <summary>
    /// Gets the kind of problem.
    /// </summary>
    public required FindingKind Kind { get; init; }

    /// <summary>
    /// Gets a human readable description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the number of affected rows.
    /// </summary>
    public required long Count { get; init; }

    /// <summary>
    /// Gets the foreign key involved, for <see cref="FindingKind.OrphanedRows"/>.
    /// </summary>
    public ForeignKeyDefinition? ForeignKey { get; init; }
}
