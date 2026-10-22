# Database optimize

A Jellyfin **12** plugin that adds a scheduled task, **Database optimize**, which reads the live
schema of `jellyfin.db`, checks the database and repairs the problems that make library scans
fail with `SQLite Error 19: 'FOREIGN KEY constraint failed'`.

The task is under *Dashboard → Scheduled Tasks → Maintenance*; its options are in the plugin
settings page (*Dashboard → Plugins → Database optimize*).

## Settings

| Option | Default | Meaning |
| --- | --- | --- |
| Execute operations on the database | off | When off, the task only logs every operation it would run and why, and sends nothing to the database (dry run). When on, the repair is executed. |
| Create a backup before the run | off | When on (and operations are executed), a copy of the database is written to the backup folder right before the repair, only if problems were found. If the backup fails, the repair is not executed. |
| Backup folder (absolute path) | empty | Folder that receives the backups. The run timestamp is added at the end of the file name, e.g. `jellyfin_20260101-030000.db`. |

Run the task once in dry run mode first and read the lines starting with `Database optimize:`
(`[DRY RUN] Would execute: ...`) before enabling the execution.

## Requirements

- Jellyfin **12.0 or newer** (built against 12.1), using the SQLite database.

## Installation

### From the plugin repository (recommended)

1. In Jellyfin go to **Dashboard → Plugins → Repositories** and click **+**.
2. Enter a name (e.g. `Database optimize`) and this URL:

   ```
   https://andreadegiovine.github.io/jellyfin-plugin-database-optimize/repository.json
   ```

3. Open **Dashboard → Plugins → Catalog**, find **Database optimize** and click **Install**.
4. **Restart Jellyfin.**

### Manual installation

1. Download the latest `database-optimize_<version>.zip` from the
   [Releases](https://github.com/andreadegiovine/jellyfin-plugin-database-optimize/releases) page.
2. Extract it into a new folder inside your Jellyfin `plugins` directory.
3. **Restart Jellyfin.**

## What the task does

The task runs every day at 03:00 (change or remove the trigger in *Scheduled Tasks*) and can be
started by hand at any time. It is skipped while a library scan is running.

1. **Reads the schema.** Tables, columns and every foreign key are read from SQLite at run time, so
   the checks follow the schema of the server you are running instead of a fixed list.
2. **Checks integrity** with `PRAGMA quick_check`. If it fails, nothing is repaired.
3. **Looks for problems:**
   - *Orphaned rows*: rows whose foreign key points to a row that no longer exists, for **every**
     foreign key of the schema.
   - *Inconsistent alternate versions* (several files of the same movie): an alternate owned by
     another alternate, `PrimaryVersionId` different from `OwnerId`, and alternate version links in
     `LinkedChildren` that do not match `BaseItems.OwnerId` or are missing.
   - *Alternate versions whose file no longer exists* although its folder does.
4. **Repairs everything in a single transaction.** Either all the changes are committed or none.
   - Orphaned rows: when the foreign key column accepts `NULL` the reference is cleared, otherwise
     the orphaned row is deleted.
   - Alternate versions are reattached to the primary item and their links are rebuilt; alternates
     whose file is gone are removed so Jellyfin can finish deleting their primary item.
5. **Checks again** and logs whether any known problem is left.

### Safeguards

- Nothing is repaired when the integrity check fails.
- A missing *folder* is never treated as a missing file (the storage may simply be unmounted), and
  the cleanup of missing alternate versions is skipped when more than half of them look missing.
- A statement that fails does not stop the others; it is logged and reported.
- Foreign keys that are composite or whose parent column cannot be resolved are reported, not
  repaired.

**Make a backup of `jellyfin.db` before the first run** (the task changes data), for example with
`sqlite3 jellyfin.db ".backup jellyfin.db.bak"` while the server is stopped.

## Troubleshooting

Every log line from this plugin starts with `Database optimize:`, so you can filter the Jellyfin
log (**Dashboard → Logs**) on that text. The log lists the number of tables and foreign keys, each
kind of problem found with its number of rows, every statement that changed rows, and what is left
at the end.

If you open an issue, please include the log lines starting with `Database optimize:`.

## Building from source

Requires the .NET 10 SDK.

```bash
dotnet restore
dotnet build -c Release
```

The output is `Jellyfin.Plugin.DatabaseOptimize/bin/Release/net10.0/Jellyfin.Plugin.DatabaseOptimize.dll`.

## Releasing

Publishing a GitHub Release runs `.github/workflows/publish.yaml`, which builds the plugin, uploads
the zip with its checksums to the release, and regenerates the plugin repository manifest
(`repository.json`) on the `gh-pages` branch. Tags use the format `vX.Y.Z.W`.

## License

Apache License 2.0, see [LICENSE](LICENSE).
