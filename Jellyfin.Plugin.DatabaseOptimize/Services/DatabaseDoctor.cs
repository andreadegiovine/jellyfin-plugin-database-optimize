using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Plugin.DatabaseOptimize.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DatabaseOptimize.Services;

/// <inheritdoc />
public sealed class DatabaseDoctor : IDatabaseDoctor
{
    private const int MaxIntegrityMessages = 20;
    private const int MaxOwnerChainPasses = 10;
    private const int MaxOrphanPasses = 3;
    private const int DeleteChunkSize = 200;
    private const int MinAlternatesForMissingFilesGuard = 20;

    private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;
    private readonly ILogger<DatabaseDoctor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseDoctor"/> class.
    /// </summary>
    /// <param name="dbContextFactory">The factory of the server database contexts.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{DatabaseDoctor}"/> interface.</param>
    public DatabaseDoctor(IDbContextFactory<JellyfinDbContext> dbContextFactory, ILogger<DatabaseDoctor> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DatabaseHealthReport> DiagnoseAsync(CancellationToken cancellationToken)
    {
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await DiagnoseCoreAsync(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<RepairResult> RepairAsync(DatabaseHealthReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var result = new RepairResult();
        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await RepairCoreAsync(context, report, result, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    result.Committed = true;
                }

                // Hand the space of the write-ahead log back to the main file. Not essential.
                try
                {
                    await context.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, Plugin.LogPrefix + "The checkpoint after the repair failed, it is not essential");
                }
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }

        return result;
    }

    private static string OrphanCondition(ForeignKeyDefinition fk)
    {
        var child = SqlText.Quote(fk.ChildTable) + "." + SqlText.Quote(fk.ChildColumn);
        return child + " IS NOT NULL AND NOT EXISTS (SELECT 1 FROM " + SqlText.Quote(fk.ParentTable)
               + " AS \"__p\" WHERE \"__p\"." + SqlText.Quote(fk.ParentColumn) + " = " + child + ")";
    }

    private static string CountOrphansSql(ForeignKeyDefinition fk)
        => "SELECT count(*) FROM " + SqlText.Quote(fk.ChildTable) + " WHERE " + OrphanCondition(fk);

    private static string FixOrphansSql(ForeignKeyDefinition fk)
        => fk.IsNullable
            ? "UPDATE " + SqlText.Quote(fk.ChildTable) + " SET " + SqlText.Quote(fk.ChildColumn) + " = NULL WHERE " + OrphanCondition(fk)
            : "DELETE FROM " + SqlText.Quote(fk.ChildTable) + " WHERE " + OrphanCondition(fk);

    private static bool IsMissingFile(string path)
    {
        if (!Path.IsPathRooted(path))
        {
            return false;
        }

        // A missing folder usually means that the storage is not mounted: never treat that as a
        // missing file.
        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrEmpty(directory) && Directory.Exists(directory) && !File.Exists(path);
    }

    private async Task<DatabaseHealthReport> DiagnoseCoreAsync(JellyfinDbContext context, CancellationToken cancellationToken)
    {
        var schema = await DatabaseSchemaReader.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        var notes = new List<string>();
        var findings = new List<DatabaseFinding>();

        var integrity = await SqliteQuery.QueryAsync(
            context,
            "PRAGMA quick_check",
            reader => SqliteQuery.Text(reader, 0),
            cancellationToken).ConfigureAwait(false);
        var integrityMessages = integrity
            .Where(m => !string.Equals(m, "ok", StringComparison.OrdinalIgnoreCase))
            .Take(MaxIntegrityMessages)
            .ToList();

        foreach (var fk in schema.ForeignKeys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fk.IsSupported)
            {
                notes.Add("Foreign key not checked (composite or unresolved): " + fk.Description);
                continue;
            }

            var count = await SqliteQuery.ScalarAsync(context, CountOrphansSql(fk), cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                findings.Add(new DatabaseFinding
                {
                    Kind = FindingKind.OrphanedRows,
                    Description = "Orphaned rows: " + fk.Description,
                    Count = count,
                    ForeignKey = fk
                });
            }
        }

        var missingAlternateIds = new List<string>();
        if (schema.HasAlternateVersionModel())
        {
            await AddAlternateFindingsAsync(context, findings, missingAlternateIds, notes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            notes.Add("The alternate versions model (BaseItems.OwnerId, PrimaryVersionId, LinkedChildren) was not found: its checks were skipped.");
        }

        return new DatabaseHealthReport
        {
            Schema = schema,
            IntegrityMessages = integrityMessages,
            Findings = findings,
            MissingAlternateIds = missingAlternateIds,
            Notes = notes
        };
    }

    private async Task AddAlternateFindingsAsync(
        JellyfinDbContext context,
        List<DatabaseFinding> findings,
        List<string> missingAlternateIds,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var checks = new (FindingKind Kind, string Description, string Sql)[]
        {
            (FindingKind.AlternateOwnerChain, "Alternate versions owned by another alternate version", AlternateVersionSql.CountOwnerChains),
            (FindingKind.AlternatePrimaryMismatch, "Alternate versions with PrimaryVersionId different from OwnerId", AlternateVersionSql.CountPrimaryMismatches),
            (FindingKind.AlternateLinkInconsistent, "Alternate version links that do not match BaseItems.OwnerId", AlternateVersionSql.CountInconsistentLinks),
            (FindingKind.AlternateLinkMissing, "Alternate versions without their link in LinkedChildren", AlternateVersionSql.CountMissingLinks)
        };

        foreach (var (kind, description, sql) in checks)
        {
            var count = await SqliteQuery.ScalarAsync(context, sql, cancellationToken).ConfigureAwait(false);
            if (count > 0)
            {
                findings.Add(new DatabaseFinding { Kind = kind, Description = description, Count = count });
            }
        }

        var alternates = await SqliteQuery.QueryAsync(
            context,
            AlternateVersionSql.ListAlternatePaths,
            reader => (Id: SqliteQuery.Text(reader, 0), Path: SqliteQuery.Text(reader, 1)),
            cancellationToken).ConfigureAwait(false);

        var missing = await Task.Run(
            () => alternates
                .Where(a => Guid.TryParse(a.Id, out _) && IsMissingFile(a.Path))
                .Select(a => a.Id)
                .ToList(),
            cancellationToken).ConfigureAwait(false);

        if (missing.Count == 0)
        {
            return;
        }

        if (alternates.Count >= MinAlternatesForMissingFilesGuard && missing.Count * 2 > alternates.Count)
        {
            notes.Add($"{missing.Count} of {alternates.Count} alternate versions have a missing file: too many, the cleanup was skipped because the storage may be unavailable.");
            return;
        }

        missingAlternateIds.AddRange(missing);
        findings.Add(new DatabaseFinding
        {
            Kind = FindingKind.AlternateFileMissing,
            Description = "Alternate versions whose file no longer exists",
            Count = missing.Count
        });
    }

    private async Task RepairCoreAsync(
        JellyfinDbContext context,
        DatabaseHealthReport report,
        RepairResult result,
        CancellationToken cancellationToken)
    {
        // 1. Orphaned rows, for every foreign key of the schema. Rows whose foreign key column
        // accepts NULL are detached (set to NULL), the others are deleted: nothing else can be
        // done with a row that points to nothing. A second pass handles what a cascade exposed.
        var orphanKeys = report.Findings
            .Where(f => f.Kind == FindingKind.OrphanedRows && f.ForeignKey is not null)
            .Select(f => f.ForeignKey!)
            .ToList();
        for (var pass = 1; pass <= MaxOrphanPasses && orphanKeys.Count > 0; pass++)
        {
            var changed = 0;
            foreach (var fk in orphanKeys)
            {
                changed += await ExecuteAsync(
                    context,
                    result,
                    (fk.IsNullable ? "Detach orphaned rows: " : "Delete orphaned rows: ") + fk.Description,
                    FixOrphansSql(fk),
                    cancellationToken).ConfigureAwait(false);
            }

            if (changed == 0)
            {
                break;
            }
        }

        if (!report.Schema.HasAlternateVersionModel())
        {
            return;
        }

        // 2. Alternate versions: owner and primary version must agree and point to the primary item.
        await ExecuteAsync(context, result, "Alternate versions: owner from PrimaryVersionId", AlternateVersionSql.OwnerFromPrimaryVersion, cancellationToken).ConfigureAwait(false);

        for (var pass = 1; pass <= MaxOwnerChainPasses; pass++)
        {
            var rows = await ExecuteAsync(context, result, "Alternate versions: flatten owner chain", AlternateVersionSql.FlattenOwnerChain, cancellationToken).ConfigureAwait(false);
            if (rows == 0)
            {
                break;
            }

            if (pass == MaxOwnerChainPasses)
            {
                _logger.LogWarning(Plugin.LogPrefix + "Owner chains still present after {Passes} passes: possible ownership cycle", pass);
            }
        }

        await ExecuteAsync(context, result, "Alternate versions: align PrimaryVersionId with OwnerId", AlternateVersionSql.AlignPrimaryVersion, cancellationToken).ConfigureAwait(false);

        // 3. Alternate versions whose file is gone. Jellyfin cannot delete them itself while
        // something still points to them, which is what produces the foreign key errors in the scans.
        foreach (var chunk in report.MissingAlternateIds.Where(id => Guid.TryParse(id, out _)).Chunk(DeleteChunkSize))
        {
            var list = string.Join(",", chunk.Select(SqlText.Literal));
            await ExecuteAsync(context, result, "Delete links of missing alternate versions", "DELETE FROM LinkedChildren WHERE ChildId IN (" + list + ") OR ParentId IN (" + list + ")", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(context, result, "Delete missing alternate versions", "DELETE FROM BaseItems WHERE Id IN (" + list + ")", cancellationToken).ConfigureAwait(false);
        }

        // 4. Alternate version links must mirror BaseItems.OwnerId.
        await ExecuteAsync(context, result, "Delete inconsistent alternate version links", AlternateVersionSql.DeleteInconsistentLinks, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(context, result, "Add missing alternate version links", AlternateVersionSql.InsertMissingLinks, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteAsync(
        JellyfinDbContext context,
        RepairResult result,
        string description,
        string sql,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await context.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
            result.Add(new RepairStep(description, rows, null));
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed statement only undoes itself: the others still run, and the failure is reported.
            _logger.LogError(ex, Plugin.LogPrefix + "Statement failed: {Description}", description);
            result.Add(new RepairStep(description, 0, ex.Message));
            return 0;
        }
    }
}
