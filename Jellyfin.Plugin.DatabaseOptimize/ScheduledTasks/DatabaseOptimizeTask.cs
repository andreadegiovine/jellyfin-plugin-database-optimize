using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.DatabaseOptimize.Models;
using Jellyfin.Plugin.DatabaseOptimize.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DatabaseOptimize.ScheduledTasks;

/// <summary>
/// Scheduled task that reads the schema of the database, checks it and repairs the foreign key
/// problems it finds.
/// </summary>
public sealed class DatabaseOptimizeTask : IScheduledTask
{
    private readonly IDatabaseDoctor _doctor;
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<DatabaseOptimizeTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseOptimizeTask"/> class.
    /// </summary>
    /// <param name="doctor">Instance of the <see cref="IDatabaseDoctor"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DatabaseOptimizeTask}"/> interface.</param>
    public DatabaseOptimizeTask(
        IDatabaseDoctor doctor,
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        ILogger<DatabaseOptimizeTask> logger)
    {
        _doctor = doctor;
        _libraryManager = libraryManager;
        _localization = localization;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Database optimize";

    /// <inheritdoc />
    public string Key => "DatabaseOptimize";

    /// <inheritdoc />
    public string Description =>
        "Reads the schema and all the foreign keys of the database, checks its integrity and repairs orphaned rows and inconsistent alternate versions.";

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // The repair changes many rows at once: do not compete with a library scan.
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogInformation(Plugin.LogPrefix + "Skipped because a library scan is running. It will run again on the next trigger.");
            return;
        }

        try
        {
            progress.Report(0);
            _logger.LogInformation(Plugin.LogPrefix + "Reading the schema and checking the database...");
            var report = await _doctor.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            LogReport(report);
            progress.Report(50);

            if (!report.IntegrityOk)
            {
                _logger.LogError(Plugin.LogPrefix + "The integrity check failed: nothing was repaired. Restore a backup or rebuild the database.");
                return;
            }

            if (!report.HasIssues)
            {
                _logger.LogInformation(Plugin.LogPrefix + "No problems found.");
                progress.Report(100);
                return;
            }

            var result = await _doctor.RepairAsync(report, cancellationToken).ConfigureAwait(false);
            LogResult(result);
            progress.Report(80);

            var after = await _doctor.DiagnoseAsync(cancellationToken).ConfigureAwait(false);
            if (after.HasIssues)
            {
                _logger.LogWarning(Plugin.LogPrefix + "{Count} problems remain after the repair", after.Findings.Count);
                LogReport(after);
            }
            else
            {
                _logger.LogInformation(Plugin.LogPrefix + "Repair completed: the database has no known problems left.");
            }

            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(Plugin.LogPrefix + "Cancelled. Changes of an unfinished repair were rolled back.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, Plugin.LogPrefix + "The task failed");
        }
    }

    private void LogReport(DatabaseHealthReport report)
    {
        var strict = report.Schema.ForeignKeys.Count(f =>
            !string.Equals(f.OnDelete, "CASCADE", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f.OnDelete, "SET NULL", StringComparison.OrdinalIgnoreCase));
        _logger.LogInformation(
            Plugin.LogPrefix + "Schema: {Tables} tables, {ForeignKeys} foreign keys ({Strict} that block the deletion of the parent row)",
            report.Schema.Tables.Count,
            report.Schema.ForeignKeys.Count,
            strict);

        if (!report.IntegrityOk)
        {
            foreach (var message in report.IntegrityMessages)
            {
                _logger.LogError(Plugin.LogPrefix + "Integrity check: {Message}", message);
            }
        }

        foreach (var finding in report.Findings)
        {
            _logger.LogWarning(Plugin.LogPrefix + "{Description}: {Count}", finding.Description, finding.Count);
        }

        foreach (var note in report.Notes)
        {
            _logger.LogInformation(Plugin.LogPrefix + "{Note}", note);
        }
    }

    private void LogResult(RepairResult result)
    {
        foreach (var step in result.Steps.Where(s => s.Error is null && s.RowsAffected > 0))
        {
            _logger.LogInformation(Plugin.LogPrefix + "{Description}: {Rows} rows", step.Description, step.RowsAffected);
        }

        foreach (var failure in result.Failures)
        {
            _logger.LogWarning(Plugin.LogPrefix + "Not done: {Description} ({Error})", failure.Description, failure.Error);
        }

        _logger.LogInformation(
            Plugin.LogPrefix + "Repair {State}: {Rows} rows changed, {Failures} statements failed",
            result.Committed ? "committed" : "not committed",
            result.TotalRows,
            result.Failures.Count);
    }
}
