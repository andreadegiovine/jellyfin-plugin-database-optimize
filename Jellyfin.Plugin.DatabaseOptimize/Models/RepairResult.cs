using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.DatabaseOptimize.Models;

/// <summary>
/// One statement executed by the repair.
/// </summary>
/// <param name="Description">What the statement does.</param>
/// <param name="RowsAffected">The number of rows changed.</param>
/// <param name="Error">The error message when the statement failed, otherwise <c>null</c>.</param>
public sealed record RepairStep(string Description, int RowsAffected, string? Error);

/// <summary>
/// The result of a repair.
/// </summary>
public sealed class RepairResult
{
    private readonly List<RepairStep> _steps = new();

    /// <summary>
    /// Gets the statements executed.
    /// </summary>
    public IReadOnlyList<RepairStep> Steps => _steps;

    /// <summary>
    /// Gets or sets a value indicating whether the transaction was committed.
    /// </summary>
    public bool Committed { get; set; }

    /// <summary>
    /// Gets the total number of rows changed.
    /// </summary>
    public long TotalRows => _steps.Where(s => s.Error is null).Sum(s => (long)s.RowsAffected);

    /// <summary>
    /// Gets the statements that failed.
    /// </summary>
    public IReadOnlyList<RepairStep> Failures => _steps.Where(s => s.Error is not null).ToList();

    /// <summary>
    /// Adds a step.
    /// </summary>
    /// <param name="step">The step.</param>
    public void Add(RepairStep step) => _steps.Add(step);
}
