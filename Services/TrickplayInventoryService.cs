using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyTPM.Models;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTPM.Services;

/// <summary>
/// Builds a short-lived, read-only snapshot of native Jellyfin trickplay coverage.
/// </summary>
public sealed class TrickplayInventoryService
{
    private const int LibraryQueryPageSize = 500;
    private const int TrickplayQueryPageSize = 1000;

    private readonly ILibraryManager _libraryManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<TrickplayInventoryService> _logger;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);

    private InventorySnapshot? _snapshot;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayInventoryService"/> class.
    /// </summary>
    public TrickplayInventoryService(
        ILibraryManager libraryManager,
        ITrickplayManager trickplayManager,
        IServerConfigurationManager serverConfigurationManager,
        ILogger<TrickplayInventoryService> logger)
    {
        _libraryManager = libraryManager;
        _trickplayManager = trickplayManager;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
    }

    /// <summary>Gets server and library coverage totals.</summary>
    public async Task<TrickplayInventorySummary> GetSummaryAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        return (await GetSnapshotAsync(refresh, cancellationToken).ConfigureAwait(false)).Summary;
    }

    /// <summary>Gets a bounded, filtered page of inventory rows.</summary>
    public async Task<PagedResult<TrickplayItemSummary>> GetItemsAsync(
        Guid? libraryId,
        TrickplayInventoryStatus? status,
        string? search,
        int startIndex,
        int limit,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(refresh, cancellationToken).ConfigureAwait(false);
        var matching = ApplyFilters(snapshot.Items, libraryId, status, search)
            .OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Year)
            .ToArray();
        var boundedStart = Math.Max(0, startIndex);
        var defaultPageSize = Math.Clamp(Plugin.Instance?.Configuration.DefaultPageSize ?? 50, 10, 100);
        var boundedLimit = Math.Clamp(limit <= 0 ? defaultPageSize : limit, 1, 100);

        return new PagedResult<TrickplayItemSummary>
        {
            Items = matching.Skip(boundedStart).Take(boundedLimit).ToArray(),
            TotalCount = matching.Length,
            StartIndex = boundedStart,
            PageSize = boundedLimit
        };
    }

    /// <summary>Gets every inventory row matching server-side dashboard filters.</summary>
    public async Task<TrickplayItemSummary[]> GetMatchingItemsAsync(
        Guid? libraryId,
        TrickplayInventoryStatus? status,
        string? search,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(refresh, cancellationToken).ConfigureAwait(false);
        return ApplyFilters(snapshot.Items, libraryId, status, search)
            .OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ParentTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SeasonName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Gets one current inventory row by Jellyfin item identifier.</summary>
    public async Task<TrickplayItemSummary?> GetItemAsync(
        Guid itemId,
        bool refresh,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(refresh, cancellationToken).ConfigureAwait(false);
        return snapshot.Items.FirstOrDefault(item => item.Id == itemId);
    }

    /// <summary>
    /// Builds one current inventory row without scanning every library item.
    /// </summary>
    internal async Task<TrickplayItemSummary?> GetCurrentItemAsync(
        Guid itemId,
        Guid? expectedLibraryId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var video = _libraryManager.GetItemById(itemId) as Video;
        if (video is null)
        {
            return null;
        }

        var containingLibraryIds = _libraryManager
            .GetCollectionFolders(video)
            .Select(folder => folder.Id)
            .ToHashSet();
        var folder = _libraryManager.GetVirtualFolders().FirstOrDefault(candidate =>
            Guid.TryParse(candidate.ItemId, out var libraryId)
            && containingLibraryIds.Contains(libraryId)
            && (!expectedLibraryId.HasValue || libraryId == expectedLibraryId.Value)
            && candidate.CollectionType is not (CollectionTypeOptions.boxsets
                or CollectionTypeOptions.music
                or CollectionTypeOptions.books));
        if (folder is null || !Guid.TryParse(folder.ItemId, out var resolvedLibraryId))
        {
            return null;
        }

        var options = folder.LibraryOptions ?? new LibraryOptions();
        var trickplayOptions = _serverConfigurationManager.Configuration.TrickplayOptions;
        var configuredWidths = (trickplayOptions.WidthResolutions ?? [])
            .Where(width => width > 0)
            .Select(NormalizeEvenWidth)
            .Where(width => width > 0)
            .Distinct()
            .Order()
            .ToArray();
        var resolutions = await _trickplayManager
            .GetTrickplayResolutions(itemId)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return BuildItem(
            video,
            resolvedLibraryId,
            folder.Name ?? string.Empty,
            options.EnableTrickplayImageExtraction,
            Plugin.Instance?.Configuration.IncludeExtras ?? false,
            trickplayOptions.Interval,
            configuredWidths,
            resolutions.Values.ToArray());
    }

    /// <summary>Invalidates the cached inventory after trickplay data changes.</summary>
    public void Invalidate()
    {
        _snapshot = null;
    }

    private static IEnumerable<TrickplayItemSummary> ApplyFilters(
        IEnumerable<TrickplayItemSummary> items,
        Guid? libraryId,
        TrickplayInventoryStatus? status,
        string? search)
    {
        var query = items;
        if (libraryId.HasValue)
        {
            query = query.Where(item => item.LibraryId == libraryId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(item => item.Status == status.Value);
        }

        var normalizedSearch = search?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(item =>
            {
                var combinedContext = string.Join(
                    " ",
                    new[] { item.ParentTitle, item.SeasonName, item.Name }
                        .Where(value => !string.IsNullOrWhiteSpace(value)));
                return item.Name.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || item.ParentTitle.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || item.SeasonName.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase)
                    || combinedContext.Contains(normalizedSearch, StringComparison.OrdinalIgnoreCase);
            });
        }

        return query;
    }

    private async Task<InventorySnapshot> GetSnapshotAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        var cacheSeconds = Math.Clamp(Plugin.Instance?.Configuration.InventoryCacheSeconds ?? 30, 5, 300);
        var observedSnapshot = _snapshot;
        var cached = observedSnapshot;
        if (!refresh
            && cached is not null
            && DateTime.UtcNow - cached.GeneratedAtUtc < TimeSpan.FromSeconds(cacheSeconds))
        {
            return cached;
        }

        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = _snapshot;
            if (refresh
                && cached is not null
                && !ReferenceEquals(cached, observedSnapshot))
            {
                return cached;
            }

            if (!refresh
                && cached is not null
                && DateTime.UtcNow - cached.GeneratedAtUtc < TimeSpan.FromSeconds(cacheSeconds))
            {
                return cached;
            }

            try
            {
                _logger.LogInformation("Building JellyTPM trickplay inventory snapshot.");
                _snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Built JellyTPM trickplay inventory snapshot for {LibraryCount} libraries and {ItemCount} items.",
                    _snapshot.Summary.LibraryCount,
                    _snapshot.Summary.TotalItems);
                return _snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to build the JellyTPM trickplay inventory snapshot.");
                throw;
            }
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private async Task<InventorySnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        var generatedAtUtc = DateTime.UtcNow;
        var trickplayOptions = _serverConfigurationManager.Configuration.TrickplayOptions;
        var configuredWidths = (trickplayOptions.WidthResolutions ?? [])
            .Where(width => width > 0)
            .Select(NormalizeEvenWidth)
            .Where(width => width > 0)
            .Distinct()
            .Order()
            .ToArray();
        var trickplayByItem = await LoadTrickplayInfoAsync(cancellationToken).ConfigureAwait(false);
        var includeExtras = Plugin.Instance?.Configuration.IncludeExtras ?? false;
        var itemRows = new List<TrickplayItemSummary>();
        var libraryRows = new List<TrickplayLibrarySummary>();

        foreach (var folder in _libraryManager.GetVirtualFolders()
                     .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folder.CollectionType is CollectionTypeOptions.boxsets
                or CollectionTypeOptions.music
                or CollectionTypeOptions.books)
            {
                // Synthetic collections duplicate linked source items, while audio and book
                // libraries are outside the scope of a native video trickplay dashboard.
                continue;
            }

            if (!Guid.TryParse(folder.ItemId, out var libraryId))
            {
                continue;
            }

            var options = folder.LibraryOptions ?? new LibraryOptions();
            var libraryItems = LoadLibraryVideos(libraryId, cancellationToken)
                .Select(video => BuildItem(
                    video,
                    libraryId,
                    folder.Name ?? string.Empty,
                    options.EnableTrickplayImageExtraction,
                    includeExtras,
                    trickplayOptions.Interval,
                    configuredWidths,
                    trickplayByItem.GetValueOrDefault(video.Id) ?? []))
                .ToArray();

            if (libraryItems.Length == 0)
            {
                continue;
            }

            itemRows.AddRange(libraryItems);
            libraryRows.Add(BuildLibrarySummary(
                libraryId,
                folder.Name ?? string.Empty,
                folder.CollectionType?.ToString() ?? string.Empty,
                options,
                libraryItems));
        }

        var enabledLibraryIds = libraryRows
            .Where(library => library.TrickplayEnabled)
            .Select(library => library.Id)
            .ToHashSet();
        var enabledEligibleItems = itemRows
            .Where(item => item.Eligible && enabledLibraryIds.Contains(item.LibraryId))
            .ToArray();
        var available = enabledEligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Available);
        var partial = enabledEligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Partial);
        var missing = enabledEligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Missing);
        var eligible = enabledEligibleItems.Length;

        return new InventorySnapshot
        {
            GeneratedAtUtc = generatedAtUtc,
            Items = itemRows.ToArray(),
            Summary = new TrickplayInventorySummary
            {
                GeneratedAtUtc = generatedAtUtc,
                ConfiguredWidths = configuredWidths,
                LibraryCount = libraryRows.Count,
                EnabledLibraryCount = enabledLibraryIds.Count,
                TotalItems = itemRows.Count,
                EligibleItems = eligible,
                AvailableItems = available,
                PartialItems = partial,
                MissingItems = missing,
                RemainingItems = partial + missing,
                CompletionPercentage = Percentage(available, eligible),
                Libraries = libraryRows.ToArray()
            }
        };
    }

    private async Task<Dictionary<Guid, List<TrickplayInfo>>> LoadTrickplayInfoAsync(
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, List<TrickplayInfo>>();
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _trickplayManager
                .GetTrickplayItemsAsync(TrickplayQueryPageSize, offset)
                .ConfigureAwait(false);

            foreach (var info in page)
            {
                if (!result.TryGetValue(info.ItemId, out var infos))
                {
                    infos = [];
                    result[info.ItemId] = infos;
                }

                infos.Add(info);
            }

            if (page.Count < TrickplayQueryPageSize)
            {
                break;
            }

            offset += page.Count;
        }

        return result;
    }

    private IEnumerable<Video> LoadLibraryVideos(Guid libraryId, CancellationToken cancellationToken)
    {
        var startIndex = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery
            {
                DescendantOfId = libraryId,
                MediaTypes = [MediaType.Video],
                SourceTypes = [SourceType.Library],
                IsVirtualItem = false,
                IsFolder = false,
                Recursive = true,
                IncludeOwnedItems = true,
                GroupByPresentationUniqueKey = false,
                StartIndex = startIndex,
                Limit = LibraryQueryPageSize
            };
            var page = _libraryManager.GetItemList(query).OfType<Video>().ToArray();
            foreach (var video in page)
            {
                yield return video;
            }

            if (page.Length < LibraryQueryPageSize)
            {
                yield break;
            }

            startIndex += page.Length;
        }
    }

    private TrickplayItemSummary BuildItem(
        Video video,
        Guid libraryId,
        string libraryName,
        bool libraryEnabled,
        bool includeExtras,
        int configuredInterval,
        IReadOnlyList<int> configuredWidths,
        IReadOnlyList<TrickplayInfo> infos)
    {
        var (eligible, eligibilityReason) = CheckEligibility(video, includeExtras, configuredInterval);
        var sourceWidth = video.GetMediaStreams()
            .Where(stream => stream.Type == MediaStreamType.Video && stream.Width > 0)
            .Select(stream => stream.Width!.Value)
            .FirstOrDefault();
        var expectedWidths = configuredWidths
            .Select(width => sourceWidth > 0 ? NormalizeEvenWidth(Math.Min(width, sourceWidth)) : width)
            .Where(width => width > 0)
            .Distinct()
            .Order()
            .ToArray();
        var resolutions = infos
            .OrderBy(info => info.Width)
            .Select(info => new TrickplayResolutionSummary
            {
                Width = info.Width,
                Height = info.Height,
                Interval = info.Interval,
                TileWidth = info.TileWidth,
                TileHeight = info.TileHeight,
                ThumbnailCount = info.ThumbnailCount
            })
            .ToArray();
        var context = ResolveItemContext(video);

        var status = ClassifyStatus(eligible, libraryEnabled, expectedWidths, resolutions);
        return new TrickplayItemSummary
        {
            Id = video.Id,
            LibraryId = libraryId,
            LibraryName = libraryName,
            Name = video.Name ?? string.Empty,
            Year = video.ProductionYear,
            ItemType = video.GetBaseItemKind().ToString(),
            ParentTitle = context.ParentTitle,
            SeasonName = context.SeasonName,
            ExtraType = video.ExtraType?.ToString() ?? string.Empty,
            RunTimeTicks = video.RunTimeTicks,
            LibraryEnabled = libraryEnabled,
            Eligible = eligible,
            EligibilityReason = eligibilityReason,
            Status = status,
            StatusLabel = GetStatusLabel(status, eligibilityReason),
            ExpectedWidths = expectedWidths,
            Resolutions = resolutions
        };
    }

    private ItemContext ResolveItemContext(Video video)
    {
        var context = video is Episode episode
            ? ResolveEpisodeContext(episode)
            : new ItemContext();

        if (video.OwnerId == Guid.Empty)
        {
            return context;
        }

        var owner = _libraryManager.GetItemById(video.OwnerId);
        if (owner is Episode ownerEpisode)
        {
            var ownerContext = ResolveEpisodeContext(ownerEpisode);
            return new ItemContext
            {
                ParentTitle = ownerContext.ParentTitle,
                SeasonName = ownerContext.SeasonName
            };
        }

        if (string.IsNullOrWhiteSpace(context.ParentTitle))
        {
            context.ParentTitle = owner?.Name ?? string.Empty;
        }

        return context;
    }

    private ItemContext ResolveEpisodeContext(Episode episode)
    {
        var seriesName = episode.SeriesName;
        if (string.IsNullOrWhiteSpace(seriesName) && episode.SeriesId != Guid.Empty)
        {
            seriesName = _libraryManager.GetItemById(episode.SeriesId)?.Name;
        }

        var seasonName = episode.SeasonName;
        if (string.IsNullOrWhiteSpace(seasonName) && episode.SeasonId != Guid.Empty)
        {
            seasonName = _libraryManager.GetItemById(episode.SeasonId)?.Name;
        }

        if (string.IsNullOrWhiteSpace(seasonName) && episode.ParentIndexNumber.HasValue)
        {
            seasonName = $"Season {episode.ParentIndexNumber.Value}";
        }

        return new ItemContext
        {
            ParentTitle = seriesName ?? string.Empty,
            SeasonName = seasonName ?? string.Empty
        };
    }

    private static (bool Eligible, string Reason) CheckEligibility(
        Video video,
        bool includeExtras,
        int configuredInterval)
    {
        if (!includeExtras && video.ExtraType.HasValue)
        {
            return (false, "Excluded extra");
        }

        if (video.VideoType is VideoType.Iso or VideoType.Dvd or VideoType.BluRay)
        {
            return (false, "Disc image video type");
        }

        if (video.IsPlaceHolder)
        {
            return (false, "Placeholder item");
        }

        if (video.IsShortcut)
        {
            return (false, "Shortcut item");
        }

        if (!video.IsCompleteMedia)
        {
            return (false, "Incomplete media");
        }

        var interval = Math.Max(1000, configuredInterval);
        if (!video.RunTimeTicks.HasValue
            || video.RunTimeTicks.Value < TimeSpan.FromMilliseconds(interval).Ticks)
        {
            return (false, "Runtime is missing or shorter than the trickplay interval");
        }

        if (video.GetMediaStreams().All(stream => stream.Type != MediaStreamType.Video))
        {
            return (false, "No video stream");
        }

        var parentDirectory = string.IsNullOrWhiteSpace(video.Path)
            ? null
            : Directory.GetParent(video.Path);
        if (string.Equals(parentDirectory?.Name, "backdrops", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Backdrop video");
        }

        return (true, string.Empty);
    }

    private static TrickplayInventoryStatus ClassifyStatus(
        bool eligible,
        bool libraryEnabled,
        IReadOnlyCollection<int> expectedWidths,
        IReadOnlyCollection<TrickplayResolutionSummary> resolutions)
    {
        if (!eligible)
        {
            return TrickplayInventoryStatus.NotEligible;
        }

        if (!libraryEnabled)
        {
            return TrickplayInventoryStatus.LibraryDisabled;
        }

        if (expectedWidths.Count == 0 || resolutions.Count == 0)
        {
            return TrickplayInventoryStatus.Missing;
        }

        var availableWidths = resolutions
            .Where(resolution => resolution.ThumbnailCount > 0)
            .Select(resolution => resolution.Width)
            .ToHashSet();
        var availableCount = expectedWidths.Count(availableWidths.Contains);
        return availableCount == expectedWidths.Count
            ? TrickplayInventoryStatus.Available
            : availableCount == 0
                ? TrickplayInventoryStatus.Missing
                : TrickplayInventoryStatus.Partial;
    }

    private static string GetStatusLabel(
        TrickplayInventoryStatus status,
        string eligibilityReason)
    {
        return status switch
        {
            TrickplayInventoryStatus.NotEligible => string.IsNullOrWhiteSpace(eligibilityReason)
                ? "Not eligible"
                : eligibilityReason,
            TrickplayInventoryStatus.LibraryDisabled => "Library disabled",
            TrickplayInventoryStatus.Missing => "Missing",
            TrickplayInventoryStatus.Partial => "Partial",
            TrickplayInventoryStatus.Available => "Available",
            _ => status.ToString()
        };
    }

    private static TrickplayLibrarySummary BuildLibrarySummary(
        Guid id,
        string name,
        string collectionType,
        LibraryOptions options,
        IReadOnlyCollection<TrickplayItemSummary> items)
    {
        var eligibleItems = items.Where(item => item.Eligible).ToArray();
        var available = eligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Available);
        var partial = eligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Partial);
        var missing = eligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Missing);

        return new TrickplayLibrarySummary
        {
            Id = id,
            Name = name,
            CollectionType = collectionType,
            TrickplayEnabled = options.EnableTrickplayImageExtraction,
            SaveWithMedia = options.SaveTrickplayWithMedia,
            TotalItems = items.Count,
            EligibleItems = eligibleItems.Length,
            AvailableItems = available,
            PartialItems = partial,
            MissingItems = missing,
            NotEligibleItems = items.Count - eligibleItems.Length,
            RemainingItems = partial + missing,
            CompletionPercentage = options.EnableTrickplayImageExtraction
                ? Percentage(available, eligibleItems.Length)
                : null
        };
    }

    private static int NormalizeEvenWidth(int width) => 2 * (width / 2);

    private static double Percentage(int numerator, int denominator)
    {
        return denominator == 0
            ? 100
            : Math.Round(100d * numerator / denominator, 1, MidpointRounding.AwayFromZero);
    }

    private sealed class InventorySnapshot
    {
        public DateTime GeneratedAtUtc { get; init; }

        public TrickplayInventorySummary Summary { get; init; } = new();

        public TrickplayItemSummary[] Items { get; init; } = [];
    }

    private sealed class ItemContext
    {
        public string ParentTitle { get; set; } = string.Empty;

        public string SeasonName { get; set; } = string.Empty;
    }
}
