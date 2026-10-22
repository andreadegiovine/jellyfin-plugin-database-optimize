using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DatabaseOptimize.Configuration;

/// <summary>
/// Plugin configuration, edited in the settings page of the plugin.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        ExecuteDatabaseOperations = false;
        BackupBeforeRun = false;
        BackupPath = string.Empty;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the repair operations are really executed on the
    /// database. When <c>false</c> (default) the task only logs every operation it would run and
    /// why, and does not change anything.
    /// </summary>
    public bool ExecuteDatabaseOperations { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a backup of the database is created before the
    /// repair. It is only used when <see cref="ExecuteDatabaseOperations"/> is enabled.
    /// </summary>
    public bool BackupBeforeRun { get; set; }

    /// <summary>
    /// Gets or sets the absolute path of the folder that receives the backups. Every backup file
    /// ends with the timestamp of the run.
    /// </summary>
    public string BackupPath { get; set; }
}
