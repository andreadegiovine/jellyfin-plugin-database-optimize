using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DatabaseOptimize.Models;

namespace Jellyfin.Plugin.DatabaseOptimize.Services;

/// <summary>
/// Checks the Jellyfin database and repairs the problems it finds.
/// </summary>
public interface IDatabaseDoctor
{
    /// <summary>
    /// Reads the schema, runs the integrity check and looks for foreign key problems and
    /// inconsistent alternate versions. Nothing is modified.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The report.</returns>
    Task<DatabaseHealthReport> DiagnoseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Repairs the problems of a report in a single transaction: either everything is committed or
    /// nothing is changed. A statement that fails is reported and skipped.
    /// </summary>
    /// <param name="report">The report returned by <see cref="DiagnoseAsync"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    Task<RepairResult> RepairAsync(DatabaseHealthReport report, CancellationToken cancellationToken);
}
