using Jellyfin.Plugin.JellyTPM.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTPM.Services;

/// <summary>Runs and validates one supported native Jellyfin trickplay operation.</summary>
public sealed class TrickplayGenerationService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly TrickplayInventoryService _inventoryService;
    private readonly ILogger<TrickplayGenerationService> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrickplayGenerationService"/> class.</summary>
    public TrickplayGenerationService(
        ILibraryManager libraryManager,
        ITrickplayManager trickplayManager,
        TrickplayInventoryService inventoryService,
        ILogger<TrickplayGenerationService> logger)
    {
        _libraryManager = libraryManager;
        _trickplayManager = trickplayManager;
        _inventoryService = inventoryService;
        _logger = logger;
    }

    /// <summary>Executes the requested trickplay operation and verifies the result.</summary>
    public async Task ExecuteAsync(TrickplayJob job, CancellationToken cancellationToken)
    {
        var inventoryItem = await _inventoryService
            .GetCurrentItemAsync(job.ItemId, job.LibraryId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new TrickplayGenerationException("The video is no longer present in an inventoried library.");

        if (!inventoryItem.Eligible)
        {
            throw new TrickplayGenerationException("The video is no longer eligible for trickplay generation.");
        }

        if (!inventoryItem.LibraryEnabled)
        {
            throw new TrickplayGenerationException("Trickplay extraction is no longer enabled for this library.");
        }

        if (job.Operation == TrickplayJobOperation.GenerateMissing
            && inventoryItem.Status == TrickplayInventoryStatus.Available)
        {
            _logger.LogInformation(
                "Skipping JellyTPM job {JobId}; item {ItemId} became complete before generation started.",
                job.Id,
                job.ItemId);
            return;
        }

        var video = _libraryManager.GetItemById(job.ItemId) as Video
            ?? throw new TrickplayGenerationException("The Jellyfin item is no longer a video.");
        var folder = _libraryManager.GetVirtualFolders().FirstOrDefault(value =>
            Guid.TryParse(value.ItemId, out var id) && id == inventoryItem.LibraryId)
            ?? throw new TrickplayGenerationException("The containing library is no longer available.");
        var libraryOptions = folder.LibraryOptions
            ?? throw new TrickplayGenerationException("The containing library has no configuration.");

        var replace = job.Operation switch
        {
            TrickplayJobOperation.GenerateMissing => false,
            TrickplayJobOperation.Regenerate => true,
            _ => throw new TrickplayGenerationException("The requested trickplay operation is not supported.")
        };

        _logger.LogInformation(
            "Starting JellyTPM {Operation} job {JobId} for item {ItemName} ({ItemId}).",
            job.Operation,
            job.Id,
            job.ItemName,
            job.ItemId);
        await _trickplayManager
            .RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken)
            .ConfigureAwait(false);

        var resolutions = await _trickplayManager
            .GetTrickplayResolutions(job.ItemId)
            .ConfigureAwait(false);
        var complete = inventoryItem.ExpectedWidths.Length > 0
            && inventoryItem.ExpectedWidths.All(width =>
                resolutions.TryGetValue(width, out var info) && info.ThumbnailCount > 0);
        if (!complete)
        {
            throw new TrickplayGenerationException(
                "Jellyfin returned from generation without creating every expected resolution. Check the Jellyfin server log.");
        }

        _inventoryService.Invalidate();
    }
}

internal sealed class TrickplayGenerationException : Exception
{
    internal TrickplayGenerationException(string message)
        : base(message)
    {
    }
}
