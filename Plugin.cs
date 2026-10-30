using System.Globalization;
using Jellyfin.Plugin.JellyTPM.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyTPM;

/// <summary>
/// Jellyfin Trickplay Manager plugin entry point.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The stable JellyTPM plugin identifier.</summary>
    public static readonly Guid PluginId = Guid.Parse("5093090e-a08a-40cb-b9e4-97de28a2d6e2");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the active plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "JellyTPM";

    /// <inheritdoc />
    public override string ConfigurationFileName => "Jellyfin.Plugin.JellyTPM.xml";

    /// <inheritdoc />
    public override string Description => "Audit and manage Jellyfin native trickplay coverage by library and item.";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>Resolves a simple file name inside the JellyTPM data directory.</summary>
    public string GetDataFilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || fileName is "." or "..")
        {
            throw new ArgumentException("Plugin data files must use a simple file name.", nameof(fileName));
        }

        return Path.Combine(DataFolderPath, fileName);
    }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = "JellyTPM",
                DisplayName = "Trickplay Dashboard",
                MenuIcon = "image_search",
                EnableInMainMenu = true,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Web.dashboard.html",
                    GetType().Namespace)
            }
        ];
    }
}

