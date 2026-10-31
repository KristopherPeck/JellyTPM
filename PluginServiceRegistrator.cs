using Jellyfin.Plugin.JellyTPM.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyTPM;

/// <summary>
/// Registers JellyTPM services with Jellyfin.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(
        IServiceCollection serviceCollection,
        IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<TrickplayInventoryService>();
        serviceCollection.AddHostedService<TrickplayInventoryService>(
            provider => provider.GetRequiredService<TrickplayInventoryService>());
        serviceCollection.AddSingleton<TrickplayJobStore>();
        serviceCollection.AddSingleton<TrickplayGenerationService>();
        serviceCollection.AddSingleton<TrickplayQueueService>();
        serviceCollection.AddHostedService<TrickplayQueueService>(
            provider => provider.GetRequiredService<TrickplayQueueService>());
    }
}
