using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DatabaseOptimize.Models;

/// <summary>
/// The result of a diagnosis of the database.
/// </summary>
public sealed class DatabaseHealthReport
{
    /// <summary>
    /// Gets the schema that was read.
    /// </summary>
    public required DatabaseSchema Schema { get; init; }

    /// <summary>
    /// Gets the messages of the SQLite quick check. Empty when the database is sound.
    /// </summary>
    public required IReadOnlyList<string> IntegrityMessages { get; init; }

    /// <summary>
    /// Gets the problems found.
    /// </summary>
    public required IReadOnlyList<DatabaseFinding> Findings { get; init; }

    /// <summary>
    /// Gets the ids of the alternate versions whose file is missing, as stored in the database.
    /// </summary>
    public required IReadOnlyList<string> MissingAlternateIds { get; init; }

    /// <summary>
    /// Gets notes about checks that were skipped or limited.
    /// </summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// Gets a value indicating whether the SQLite quick check passed.
    /// </summary>
    public bool IntegrityOk => IntegrityMessages.Count == 0;

    /// <summary>
    /// Gets a value indicating whether any problem was found.
    /// </summary>
    public bool HasIssues => Findings.Count > 0;

    /// <summary>
    /// Gets the total number of affected rows.
    /// </summary>
    public long TotalRows => Findings.Sum(f => f.Count);
}
