using Jellyfin.Plugin.JellyTPM.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTPM.Services;

/// <summary>Runs the durable trickplay generation queue with one worker.</summary>
public sealed class TrickplayQueueService : BackgroundService
{
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly TrickplayInventoryService _inventoryService;
    private readonly TrickplayGenerationService _generationService;
    private readonly TrickplayJobStore _jobStore;
    private readonly ILogger<TrickplayQueueService> _logger;

    /// <summary>Initializes a new instance of the <see cref="TrickplayQueueService"/> class.</summary>
    public TrickplayQueueService(
        TrickplayInventoryService inventoryService,
        TrickplayGenerationService generationService,
        TrickplayJobStore jobStore,
        ILogger<TrickplayQueueService> logger)
    {
        _inventoryService = inventoryService;
        _generationService = generationService;
        _jobStore = jobStore;
        _logger = logger;
    }

    /// <summary>Validates and queues missing generation for one video.</summary>
    public async Task<TrickplayEnqueueResult> EnqueueMissingAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = await _inventoryService.GetCurrentItemAsync(itemId, null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The video is not present in the JellyTPM inventory.");
        if (!item.Eligible)
        {
            throw new InvalidOperationException("The video is not eligible for trickplay generation.");
        }

        if (!item.LibraryEnabled)
        {
            throw new InvalidOperationException("Trickplay extraction is disabled for this library.");
        }

        if (item.Status == TrickplayInventoryStatus.Available)
        {
            throw new InvalidOperationException("The video already has every expected trickplay resolution.");
        }

        var result = await _jobStore
            .EnqueueAsync(item, TrickplayJobOperation.GenerateMissing, cancellationToken)
            .ConfigureAwait(false);
        if (result.Created)
        {
            WakeWorker();
        }

        return result;
    }

    /// <summary>Validates and queues destructive replacement generation for one video.</summary>
    public async Task<TrickplayEnqueueResult> EnqueueRegenerateAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var item = await _inventoryService.GetCurrentItemAsync(itemId, null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("The video is not present in the JellyTPM inventory.");
        if (!item.Eligible)
        {
            throw new InvalidOperationException("The video is not eligible for trickplay regeneration.");
        }

        if (!item.LibraryEnabled)
        {
            throw new InvalidOperationException("Trickplay extraction is disabled for this library.");
        }

        if (!item.Resolutions.Any(resolution => resolution.ThumbnailCount > 0))
        {
            throw new InvalidOperationException("The video has no existing trickplay data to replace.");
        }

        var result = await _jobStore
            .EnqueueAsync(item, TrickplayJobOperation.Regenerate, cancellationToken)
            .ConfigureAwait(false);
        if (result.Created)
        {
            WakeWorker();
        }

        return result;
    }

    /// <summary>Queues actionable Missing and Partial items from the current server-side filters.</summary>
    public async Task<TrickplayBatchEnqueueResult> EnqueueFilteredAsync(
        Guid? libraryId,
        TrickplayInventoryStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var matching = await _inventoryService
            .GetMatchingItemsAsync(libraryId, status, search, true, cancellationToken)
            .ConfigureAwait(false);
        var candidates = matching
            .Where(item =>
                item.Eligible
                && item.LibraryEnabled
                && item.Status is TrickplayInventoryStatus.Missing or TrickplayInventoryStatus.Partial)
            .ToArray();
        var batchLimit = Math.Clamp(Plugin.Instance?.Configuration.MaximumBatchSize ?? 500, 1, 5000);
        var selected = candidates.Take(batchLimit).ToArray();
        var enqueueResult = await _jobStore.EnqueueManyAsync(selected, cancellationToken).ConfigureAwait(false);
        if (enqueueResult.QueuedItems > 0)
        {
            WakeWorker();
        }

        return new TrickplayBatchEnqueueResult
        {
            MatchingItems = matching.Length,
            CandidateItems = candidates.Length,
            QueuedItems = enqueueResult.QueuedItems,
            AlreadyActiveItems = enqueueResult.AlreadyActiveItems,
            BatchLimit = batchLimit,
            Truncated = candidates.Length > batchLimit,
            QueueCapacityLimitedItems = enqueueResult.QueueCapacityLimitedItems,
            ActiveQueueLimit = enqueueResult.ActiveQueueLimit
        };
    }

    /// <summary>Gets a bounded page of queue and history records.</summary>
    public Task<TrickplayJobPage> GetJobsAsync(
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        return _jobStore.GetJobsAsync(startIndex, limit, cancellationToken);
    }

    /// <summary>Cancels one queued job.</summary>
    public Task<bool> CancelQueuedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return _jobStore.CancelQueuedAsync(jobId, cancellationToken);
    }

    /// <summary>Gets one durable queue or history record.</summary>
    public Task<TrickplayJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return _jobStore.GetJobAsync(jobId, cancellationToken);
    }

    /// <summary>Clears terminal queue history while preserving all active jobs.</summary>
    public Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        return _jobStore.ClearHistoryAsync(cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var recovered = await _jobStore.RecoverInterruptedAsync(stoppingToken).ConfigureAwait(false);
            if (recovered > 0)
            {
                _logger.LogInformation(
                    "Returned {JobCount} interrupted JellyTPM generation jobs to the queue.",
                    recovered);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to recover interrupted JellyTPM generation jobs.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await _jobStore.ClaimNextAsync(stoppingToken).ConfigureAwait(false);
                if (job is null)
                {
                    await _wakeSignal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await ProcessJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "The JellyTPM queue worker encountered an unexpected error.");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ProcessJobAsync(TrickplayJob job, CancellationToken stoppingToken)
    {
        try
        {
            await _generationService.ExecuteAsync(job, stoppingToken).ConfigureAwait(false);
            await _jobStore.CompleteAsync(job.Id, stoppingToken).ConfigureAwait(false);
            _inventoryService.Invalidate();
            _logger.LogInformation(
                "Completed JellyTPM {Operation} job {JobId} for item {ItemName} ({ItemId}).",
                job.Operation,
                job.Id,
                job.ItemName,
                job.ItemId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Startup recovery returns the still-running durable job to the queue.
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "JellyTPM {Operation} job {JobId} failed for item {ItemName} ({ItemId}).",
                job.Operation,
                job.Id,
                job.ItemName,
                job.ItemId);
            await _jobStore.FailAsync(
                job.Id,
                GetSafeFailureMessage(exception),
                stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            _inventoryService.Invalidate();
        }
    }

    private void WakeWorker()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // The worker is already awake or has a pending wake-up.
        }
    }

    private static string GetSafeFailureMessage(Exception exception)
    {
        return exception is TrickplayGenerationException
            ? exception.Message
            : "Generation failed unexpectedly. Check the Jellyfin server log for details.";
    }
}
