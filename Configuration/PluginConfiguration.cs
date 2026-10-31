using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTPM.Configuration;

/// <summary>
/// Configures JellyTPM inventory and generation behavior.
/// </summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin extras are included in coverage totals.
    /// </summary>
    public bool IncludeExtras { get; set; }

    /// <summary>
    /// Gets or sets the number of seconds an inventory snapshot may be reused.
    /// </summary>
    public int InventoryCacheSeconds { get; set; } = 900;

    /// <summary>
    /// Gets or sets the default number of rows returned by the item endpoint.
    /// </summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>
    /// Gets or sets the number of days completed generation history is retained.
    /// </summary>
    public int JobHistoryRetentionDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the maximum number of terminal generation jobs retained.
    /// </summary>
    public int MaximumRetainedJobs { get; set; } = 50;

    /// <summary>
    /// Gets or sets a value indicating whether the completed total survives history pruning and clearing.
    /// </summary>
    public bool PreserveCompletedCount { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of candidates accepted by one filtered batch request.
    /// </summary>
    public int MaximumBatchSize { get; set; } = 500;

    /// <summary>
    /// Gets or sets the maximum number of queued and generating jobs retained at once.
    /// </summary>
    public int MaximumActiveJobs { get; set; } = 1000;
}

