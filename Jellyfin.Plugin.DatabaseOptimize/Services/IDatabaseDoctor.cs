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
    /// <param name="execute">
    /// <c>true</c> to run the statements on the database. <c>false</c> for a dry run: every
    /// operation that would be executed is logged with the reason why it is needed, and nothing is
    /// sent to the database.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was done.</returns>
    Task<RepairResult> RepairAsync(DatabaseHealthReport report, bool execute, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a consistent copy of the database in a folder. The timestamp of the backup is added
    /// at the end of the file name.
    /// </summary>
    /// <param name="directory">The absolute path of the folder that receives the backup.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path of the backup file.</returns>
    Task<string> BackupAsync(string directory, CancellationToken cancellationToken);
}
