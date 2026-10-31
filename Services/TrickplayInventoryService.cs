using System.Diagnostics;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTPM.Services;

/// <summary>
/// Builds and refreshes a cached, read-only snapshot of native Jellyfin trickplay coverage.
/// </summary>
public sealed class TrickplayInventoryService : BackgroundService
{
    private const int LibraryQueryPageSize = 500;
    private const int TrickplayQueryPageSize = 1000;

    private readonly ILibraryManager _libraryManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<TrickplayInventoryService> _logger;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly object _refreshStateLock = new();

    private volatile InventorySnapshot? _snapshot;
    private volatile bool _snapshotInvalidated;
    private long _snapshotChangeVersion;
    private bool _refreshInProgress;
    private DateTime? _refreshStartedAtUtc;
    private DateTime? _refreshCompletedAtUtc;
    private string _refreshErrorMessage = string.Empty;
    private InventorySnapshot? _refreshObservedSnapshot;
    private TaskCompletionSource<bool>? _refreshCompletion;
    private CancellationTokenSource? _refreshCancellation;

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
        return (await GetSnapshotAsync(
            refresh,
            reuseExistingSnapshot: true,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Summary;
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
        var snapshot = await GetSnapshotAsync(
            refresh,
            reuseExistingSnapshot: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Gets every inventory row matching server-side dashboard filters from the latest completed snapshot.
    /// </summary>
    public async Task<TrickplayItemSummary[]> GetMatchingItemsAsync(
        Guid? libraryId,
        TrickplayInventoryStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(
            refresh: false,
            reuseExistingSnapshot: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
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
        var snapshot = await GetSnapshotAsync(
            refresh,
            reuseExistingSnapshot: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return snapshot.Items.FirstOrDefault(item => item.Id == itemId);
    }

    /// <summary>Queues one server-side inventory refresh, coalescing duplicate requests.</summary>
    public TrickplayInventoryRefreshStatus RequestRefresh()
    {
        lock (_refreshStateLock)
        {
            EnsureRefreshRequested();
            return CreateRefreshStatus();
        }
    }

    /// <summary>
    /// Queues or joins the current server-side refresh and waits for it to finish.
    /// </summary>
    public async Task RefreshAndWaitAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        Task<bool> completion;
        lock (_refreshStateLock)
        {
            EnsureRefreshRequested();
            completion = _refreshCompletion!.Task;
        }

        progress.Report(0);
        using var cancellationRegistration = cancellationToken.Register(CancelCurrentRefresh);
        var succeeded = await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!succeeded)
        {
            throw new InvalidOperationException(
                "The JellyTPM inventory refresh failed. Check the Jellyfin server log for details.");
        }

        progress.Report(100);
    }

    /// <summary>Gets the current server-side inventory refresh status.</summary>
    public TrickplayInventoryRefreshStatus GetRefreshStatus()
    {
        lock (_refreshStateLock)
        {
            return CreateRefreshStatus();
        }
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

    /// <summary>
    /// Replaces one cached inventory row and atomically recalculates its library and server totals.
    /// </summary>
    internal async Task<bool> UpdateCachedItemAsync(
        Guid itemId,
        Guid expectedLibraryId,
        CancellationToken cancellationToken)
    {
        var currentItem = await GetCurrentItemAsync(itemId, expectedLibraryId, cancellationToken)
            .ConfigureAwait(false);
        if (currentItem is null)
        {
            return false;
        }

        if (!await _snapshotLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Do not hold up the generation queue behind a potentially long full-library scan.
            return false;
        }

        try
        {
            var snapshot = _snapshot;
            if (snapshot is null)
            {
                return false;
            }

            var itemIndex = Array.FindIndex(snapshot.Items, item => item.Id == itemId);
            if (itemIndex < 0 || snapshot.Items[itemIndex].LibraryId != expectedLibraryId)
            {
                return false;
            }

            var librarySummary = snapshot.Summary.Libraries
                .FirstOrDefault(library => library.Id == expectedLibraryId);
            if (librarySummary is null || librarySummary.TrickplayEnabled != currentItem.LibraryEnabled)
            {
                return false;
            }

            var updatedItems = snapshot.Items.ToArray();
            updatedItems[itemIndex] = currentItem;
            _snapshot = new InventorySnapshot
            {
                GeneratedAtUtc = snapshot.GeneratedAtUtc,
                Items = updatedItems,
                Summary = RebuildSummary(snapshot.Summary, updatedItems, expectedLibraryId)
            };
            return true;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>Invalidates the cached inventory after trickplay data changes.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _snapshotChangeVersion);
        _snapshotInvalidated = true;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _refreshSignal.WaitAsync(stoppingToken).ConfigureAwait(false);
                await ProcessQueuedRefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task ProcessQueuedRefreshAsync(CancellationToken stoppingToken)
    {
        InventorySnapshot? observedSnapshot;
        TaskCompletionSource<bool> completion;
        CancellationTokenSource refreshCancellation;
        lock (_refreshStateLock)
        {
            observedSnapshot = _refreshObservedSnapshot;
            completion = _refreshCompletion!;
            refreshCancellation = _refreshCancellation!;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            refreshCancellation.Token);
        try
        {
            var snapshot = await RefreshInBackgroundAsync(observedSnapshot, linkedCancellation.Token)
                .ConfigureAwait(false);
            CompleteRefresh(string.Empty);
            completion.TrySetResult(true);
            _logger.LogInformation(
                "JellyTPM server-side inventory refresh completed with snapshot {GeneratedAtUtc}.",
                snapshot.GeneratedAtUtc);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            var message = stoppingToken.IsCancellationRequested
                ? string.Empty
                : "Inventory refresh was cancelled.";
            CompleteRefresh(message);
            completion.TrySetCanceled(linkedCancellation.Token);
            if (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "JellyTPM server-side inventory refresh failed.");
            CompleteRefresh("Inventory refresh failed. Check the Jellyfin server log for details.");
            completion.TrySetResult(false);
        }
        finally
        {
            refreshCancellation.Dispose();
        }
    }

    private void EnsureRefreshRequested()
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        _refreshStartedAtUtc = DateTime.UtcNow;
        _refreshCompletedAtUtc = null;
        _refreshErrorMessage = string.Empty;
        _refreshObservedSnapshot = _snapshot;
        _refreshCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshCancellation = new CancellationTokenSource();
        _refreshSignal.Release();
    }

    private void CancelCurrentRefresh()
    {
        CancellationTokenSource? refreshCancellation;
        lock (_refreshStateLock)
        {
            refreshCancellation = _refreshCancellation;
        }

        refreshCancellation?.Cancel();
    }

    private void CompleteRefresh(string errorMessage)
    {
        lock (_refreshStateLock)
        {
            _refreshInProgress = false;
            _refreshCompletedAtUtc = DateTime.UtcNow;
            _refreshErrorMessage = errorMessage;
            _refreshObservedSnapshot = null;
            _refreshCompletion = null;
            _refreshCancellation = null;
        }
    }

    private async Task<InventorySnapshot> RefreshInBackgroundAsync(
        InventorySnapshot? observedSnapshot,
        CancellationToken cancellationToken)
    {
        await _snapshotLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cached = _snapshot;
            if (cached is not null && !ReferenceEquals(cached, observedSnapshot))
            {
                return cached;
            }

            _logger.LogInformation("Building JellyTPM trickplay inventory snapshot in the server-side worker.");
            var changeVersion = Volatile.Read(ref _snapshotChangeVersion);
            var snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
            _snapshot = snapshot;
            _snapshotInvalidated = Volatile.Read(ref _snapshotChangeVersion) != changeVersion;
            return snapshot;
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    private TrickplayInventoryRefreshStatus CreateRefreshStatus()
    {
        var snapshot = _snapshot;
        return new TrickplayInventoryRefreshStatus
        {
            IsRefreshing = _refreshInProgress,
            HasSnapshot = snapshot is not null,
            IsSnapshotStale = _snapshotInvalidated,
            StartedAtUtc = _refreshStartedAtUtc,
            CompletedAtUtc = _refreshCompletedAtUtc,
            SnapshotGeneratedAtUtc = snapshot?.GeneratedAtUtc,
            ErrorMessage = _refreshErrorMessage
        };
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
        bool reuseExistingSnapshot,
        CancellationToken cancellationToken)
    {
        var cacheSeconds = Math.Clamp(Plugin.Instance?.Configuration.InventoryCacheSeconds ?? 900, 5, 3600);
        var observedSnapshot = _snapshot;
        var cached = observedSnapshot;
        if (!refresh
            && cached is not null
            && (reuseExistingSnapshot
                || (!_snapshotInvalidated
                    && DateTime.UtcNow - cached.GeneratedAtUtc < TimeSpan.FromSeconds(cacheSeconds))))
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
                && (reuseExistingSnapshot
                    || (!_snapshotInvalidated
                        && DateTime.UtcNow - cached.GeneratedAtUtc < TimeSpan.FromSeconds(cacheSeconds))))
            {
                return cached;
            }

            try
            {
                _logger.LogInformation("Building JellyTPM trickplay inventory snapshot.");
                var changeVersion = Volatile.Read(ref _snapshotChangeVersion);
                _snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
                _snapshotInvalidated = Volatile.Read(ref _snapshotChangeVersion) != changeVersion;
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
        var snapshotStopwatch = Stopwatch.StartNew();
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
            var libraryQueryElapsed = TimeSpan.Zero;
            var libraryBuildStopwatch = Stopwatch.StartNew();
            var libraryItems = LoadLibraryVideos(
                    libraryId,
                    elapsed => libraryQueryElapsed += elapsed,
                    cancellationToken)
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
            libraryBuildStopwatch.Stop();
            var inventoryConstructionElapsed = libraryBuildStopwatch.Elapsed - libraryQueryElapsed;
            _logger.LogInformation(
                "Queried Jellyfin library {LibraryName} and loaded {VideoCount} videos in {ElapsedSeconds:F1} seconds.",
                folder.Name ?? string.Empty,
                libraryItems.Length,
                libraryQueryElapsed.TotalSeconds);
            _logger.LogInformation(
                "Constructed inventory rows for library {LibraryName} with {VideoCount} videos in {ElapsedSeconds:F1} seconds.",
                folder.Name ?? string.Empty,
                libraryItems.Length,
                inventoryConstructionElapsed.TotalSeconds);

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

        var completedAtUtc = DateTime.UtcNow;
        var snapshot = new InventorySnapshot
        {
            GeneratedAtUtc = completedAtUtc,
            Items = itemRows.ToArray(),
            Summary = new TrickplayInventorySummary
            {
                GeneratedAtUtc = completedAtUtc,
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
        snapshotStopwatch.Stop();
        _logger.LogInformation(
            "Built JellyTPM trickplay inventory snapshot for {LibraryCount} libraries and {ItemCount} items in {ElapsedSeconds:F1} seconds.",
            snapshot.Summary.LibraryCount,
            snapshot.Summary.TotalItems,
            snapshotStopwatch.Elapsed.TotalSeconds);
        return snapshot;
    }

    private async Task<Dictionary<Guid, List<TrickplayInfo>>> LoadTrickplayInfoAsync(
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new Dictionary<Guid, List<TrickplayInfo>>();
        var offset = 0;
        var recordCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _trickplayManager
                .GetTrickplayItemsAsync(TrickplayQueryPageSize, offset)
                .ConfigureAwait(false);

            foreach (var info in page)
            {
                recordCount++;
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

        stopwatch.Stop();
        _logger.LogInformation(
            "Loaded {TrickplayRecordCount} native trickplay records for {ItemCount} items in {ElapsedSeconds:F1} seconds.",
            recordCount,
            result.Count,
            stopwatch.Elapsed.TotalSeconds);
        return result;
    }

    private IEnumerable<Video> LoadLibraryVideos(
        Guid libraryId,
        Action<TimeSpan> recordQueryElapsed,
        CancellationToken cancellationToken)
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
            var queryStopwatch = Stopwatch.StartNew();
            var page = _libraryManager.GetItemList(query).OfType<Video>().ToArray();
            queryStopwatch.Stop();
            recordQueryElapsed(queryStopwatch.Elapsed);
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

    private static TrickplayInventorySummary RebuildSummary(
        TrickplayInventorySummary existingSummary,
        IReadOnlyCollection<TrickplayItemSummary> items,
        Guid affectedLibraryId)
    {
        var libraries = existingSummary.Libraries
            .Select(library => library.Id == affectedLibraryId
                ? RebuildLibrarySummary(
                    library,
                    items.Where(item => item.LibraryId == affectedLibraryId).ToArray())
                : library)
            .ToArray();
        var enabledLibraryIds = libraries
            .Where(library => library.TrickplayEnabled)
            .Select(library => library.Id)
            .ToHashSet();
        var enabledEligibleItems = items
            .Where(item => item.Eligible && enabledLibraryIds.Contains(item.LibraryId))
            .ToArray();
        var available = enabledEligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Available);
        var partial = enabledEligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Partial);
        var missing = enabledEligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Missing);

        return new TrickplayInventorySummary
        {
            GeneratedAtUtc = existingSummary.GeneratedAtUtc,
            ConfiguredWidths = existingSummary.ConfiguredWidths,
            LibraryCount = libraries.Length,
            EnabledLibraryCount = enabledLibraryIds.Count,
            TotalItems = items.Count,
            EligibleItems = enabledEligibleItems.Length,
            AvailableItems = available,
            PartialItems = partial,
            MissingItems = missing,
            RemainingItems = partial + missing,
            CompletionPercentage = Percentage(available, enabledEligibleItems.Length),
            Libraries = libraries
        };
    }

    private static TrickplayLibrarySummary RebuildLibrarySummary(
        TrickplayLibrarySummary existingSummary,
        IReadOnlyCollection<TrickplayItemSummary> items)
    {
        var eligibleItems = items.Where(item => item.Eligible).ToArray();
        var available = eligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Available);
        var partial = eligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Partial);
        var missing = eligibleItems.Count(item => item.Status == TrickplayInventoryStatus.Missing);

        return new TrickplayLibrarySummary
        {
            Id = existingSummary.Id,
            Name = existingSummary.Name,
            CollectionType = existingSummary.CollectionType,
            TrickplayEnabled = existingSummary.TrickplayEnabled,
            SaveWithMedia = existingSummary.SaveWithMedia,
            TotalItems = items.Count,
            EligibleItems = eligibleItems.Length,
            AvailableItems = available,
            PartialItems = partial,
            MissingItems = missing,
            NotEligibleItems = items.Count - eligibleItems.Length,
            RemainingItems = partial + missing,
            CompletionPercentage = existingSummary.TrickplayEnabled
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
