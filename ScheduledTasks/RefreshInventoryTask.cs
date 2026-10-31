using Jellyfin.Plugin.JellyTPM.Services;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyTPM.ScheduledTasks;

/// <summary>Refreshes the cached JellyTPM inventory through Jellyfin's task scheduler.</summary>
public sealed class RefreshInventoryTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly TrickplayInventoryService _inventoryService;

    /// <summary>Initializes a new instance of the <see cref="RefreshInventoryTask"/> class.</summary>
    public RefreshInventoryTask(TrickplayInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    /// <inheritdoc />
    public string Name => "Refresh JellyTPM inventory";

    /// <inheritdoc />
    public string Key => "JellyTPMRefreshInventory";

    /// <inheritdoc />
    public string Description => "Refreshes cached native trickplay coverage for all inventoried video libraries.";

    /// <inheritdoc />
    public string Category => "JellyTPM";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return _inventoryService.RefreshAndWaitAsync(progress, cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }
}
