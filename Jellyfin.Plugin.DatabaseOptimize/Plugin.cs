using System;
using System.Collections.Generic;
using Jellyfin.Plugin.DatabaseOptimize.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DatabaseOptimize;

/// <summary>
/// Main plugin class. The plugin contributes a scheduled task
/// (<see cref="ScheduledTasks.DatabaseOptimizeTask"/>) and a settings page.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Prefix prepended to every log message of this plugin, to make it easy to find in the server log.
    /// </summary>
    public const string LogPrefix = "Database optimize: ";

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Database optimize";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("1d641174-c27f-4b9c-be54-f5261fec5b28");

    /// <inheritdoc />
    public override string Description =>
        "Checks the Jellyfin database and repairs foreign key problems with a scheduled task.";

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
