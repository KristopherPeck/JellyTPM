using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyTPM.Configuration;

/// <summary>
/// Configures JellyTPM's read-only inventory behavior.
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
    public int InventoryCacheSeconds { get; set; } = 30;

    /// <summary>
    /// Gets or sets the default number of rows returned by the item endpoint.
    /// </summary>
    public int DefaultPageSize { get; set; } = 50;
}

