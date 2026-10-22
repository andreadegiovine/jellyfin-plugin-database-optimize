using System;
using System.Collections.Generic;
using System.Globalization;
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
    public async Task<string> BackupAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
        {
            throw new InvalidOperationException("The backup path must be a non-empty absolute path.");
        }

        Directory.CreateDirectory(directory);

        var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // The file of the main database, to name the backup after it.
                var databases = await SqliteQuery.QueryAsync(
                    context,
                    "PRAGMA database_list",
                    reader => (Name: SqliteQuery.Text(reader, 1), File: SqliteQuery.Text(reader, 2)),
                    cancellationToken).ConfigureAwait(false);
                var source = databases
                    .Where(d => string.Equals(d.Name, "main", StringComparison.OrdinalIgnoreCase))
                    .Select(d => d.File)
                    .FirstOrDefault() ?? string.Empty;

                var name = Path.GetFileNameWithoutExtension(source);
                var extension = Path.GetExtension(source);
                var target = Path.Combine(
                    directory,
                    (string.IsNullOrEmpty(name) ? "jellyfin" : name)
                    + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                    + (string.IsNullOrEmpty(extension) ? ".db" : extension));

                if (File.Exists(target))
                {
                    throw new IOException("The backup file already exists: " + target);
                }

                // VACUUM INTO writes a consistent copy of the database, including what is still in
                // the write-ahead log, while the server keeps running. A DbCommand is used because
                // the raw SQL helpers of EF Core would interpret braces in the path.
                var command = context.Database.GetDbConnection().CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.CommandText = "VACUUM INTO " + SqlText.Literal(target);
                    command.CommandTimeout = 3600;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                return target;
            }
            finally
            {
                await context.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task<RepairResult> RepairAsync(DatabaseHealthReport report, bool execute, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var result = new RepairResult();

        if (!execute)
        {
            // Dry run: no context, no connection and no transaction are created, so nothing can
            // reach the database. Every operation is only logged.
            await RepairCoreAsync(null, report, result, cancellationToken).ConfigureAwait(false);
            return result;
        }

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

    private static long CountOf(DatabaseHealthReport report, FindingKind kind)
        => report.Findings.Where(f => f.Kind == kind).Sum(f => f.Count);

    // A null context means dry run: the statements are logged and never executed.
    private async Task RepairCoreAsync(
        JellyfinDbContext? context,
        DatabaseHealthReport report,
        RepairResult result,
        CancellationToken cancellationToken)
    {
        // 1. Orphaned rows, for every foreign key of the schema. Rows whose foreign key column
        // accepts NULL are detached (set to NULL), the others are deleted: nothing else can be
        // done with a row that points to nothing. A second pass handles what a cascade exposed.
        var orphanFindings = report.Findings
            .Where(f => f.Kind == FindingKind.OrphanedRows && f.ForeignKey is not null)
            .ToList();
        for (var pass = 1; pass <= MaxOrphanPasses && orphanFindings.Count > 0; pass++)
        {
            var changed = 0;
            foreach (var finding in orphanFindings)
            {
                var fk = finding.ForeignKey!;
                var reason = $"rows of {fk.ChildTable} point to a {fk.ParentTable} row that does not exist (foreign key violation, SQLite Error 19 when Jellyfin touches them); "
                    + (fk.IsNullable
                        ? $"{fk.ChildColumn} accepts NULL, so the reference is cleared"
                        : $"{fk.ChildColumn} does not accept NULL, so the rows are deleted");
                changed += await ExecuteAsync(
                    context,
                    result,
                    (fk.IsNullable ? "Detach orphaned rows: " : "Delete orphaned rows: ") + fk.Description,
                    reason,
                    FixOrphansSql(fk),
                    finding.Count,
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
        await ExecuteAsync(
            context,
            result,
            "Alternate versions: owner from PrimaryVersionId",
            "OwnerId differs from PrimaryVersionId and PrimaryVersionId points to a primary item, so the alternate belongs to that item",
            AlternateVersionSql.OwnerFromPrimaryVersion,
            null,
            cancellationToken).ConfigureAwait(false);

        for (var pass = 1; pass <= MaxOwnerChainPasses; pass++)
        {
            var rows = await ExecuteAsync(
                context,
                result,
                "Alternate versions: flatten owner chain",
                "an alternate version is owned by another alternate version instead of the primary item (moved one level up, repeated until no row changes)",
                AlternateVersionSql.FlattenOwnerChain,
                CountOf(report, FindingKind.AlternateOwnerChain),
                cancellationToken).ConfigureAwait(false);
            if (rows == 0)
            {
                break;
            }

            if (pass == MaxOwnerChainPasses)
            {
                _logger.LogWarning(Plugin.LogPrefix + "Owner chains still present after {Passes} passes: possible ownership cycle", pass);
            }
        }

        await ExecuteAsync(
            context,
            result,
            "Alternate versions: align PrimaryVersionId with OwnerId",
            "PrimaryVersionId is NULL or different from OwnerId on an alternate version",
            AlternateVersionSql.AlignPrimaryVersion,
            CountOf(report, FindingKind.AlternatePrimaryMismatch),
            cancellationToken).ConfigureAwait(false);

        // 3. Alternate versions whose file is gone. Jellyfin cannot delete them itself while
        // something still points to them, which is what produces the foreign key errors in the scans.
        foreach (var chunk in report.MissingAlternateIds.Where(id => Guid.TryParse(id, out _)).Chunk(DeleteChunkSize))
        {
            var list = string.Join(",", chunk.Select(SqlText.Literal));
            await ExecuteAsync(
                context,
                result,
                "Delete links of missing alternate versions",
                "the file of these alternate versions no longer exists (its folder does), and Jellyfin cannot delete them while links still point to them",
                "DELETE FROM LinkedChildren WHERE ChildId IN (" + list + ") OR ParentId IN (" + list + ")",
                null,
                cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                context,
                result,
                "Delete missing alternate versions",
                "the file of these alternate versions no longer exists (its folder does)",
                "DELETE FROM BaseItems WHERE Id IN (" + list + ")",
                chunk.Length,
                cancellationToken).ConfigureAwait(false);
        }

        // 4. Alternate version links must mirror BaseItems.OwnerId.
        await ExecuteAsync(
            context,
            result,
            "Delete inconsistent alternate version links",
            "LinkedChildren alternate version links whose child is not owned by the parent of the link",
            AlternateVersionSql.DeleteInconsistentLinks,
            CountOf(report, FindingKind.AlternateLinkInconsistent),
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            context,
            result,
            "Add missing alternate version links",
            "alternate versions have no LinkedChildren link from their primary item",
            AlternateVersionSql.InsertMissingLinks,
            CountOf(report, FindingKind.AlternateLinkMissing),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> ExecuteAsync(
        JellyfinDbContext? context,
        RepairResult result,
        string description,
        string reason,
        string sql,
        long? matching,
        CancellationToken cancellationToken)
    {
        if (context is null)
        {
            // Dry run: say what would be done and why. The row count is what matches in the
            // current, unmodified database, so the counts of later steps ignore earlier ones.
            _logger.LogInformation(
                Plugin.LogPrefix + "[DRY RUN] Would execute: {Description} | Reason: {Reason} | Rows matching now: {Rows} | SQL: {Sql}",
                description,
                reason,
                matching.HasValue ? matching.Value.ToString(CultureInfo.InvariantCulture) : "unknown",
                sql);
            result.Add(new RepairStep(description, 0, null));
            return 0;
        }

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
