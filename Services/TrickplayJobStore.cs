using System.Text.Json;
using Jellyfin.Plugin.JellyTPM.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTPM.Services;

/// <summary>Persists the generation queue and retained history in plugin-owned storage.</summary>
public sealed class TrickplayJobStore
{
    private const string StoreFileName = "generation-jobs.json";
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<TrickplayJobStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<TrickplayJob>? _jobs;

    /// <summary>Initializes a new instance of the <see cref="TrickplayJobStore"/> class.</summary>
    public TrickplayJobStore(ILogger<TrickplayJobStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Creates a job unless an active job already exists for the item.</summary>
    public async Task<TrickplayEnqueueResult> EnqueueAsync(
        TrickplayItemSummary item,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var existing = jobs.FirstOrDefault(job =>
                job.ItemId == item.Id
                && job.Operation == TrickplayJobOperation.GenerateMissing
                && job.Status is TrickplayJobStatus.Queued or TrickplayJobStatus.Generating);
            if (existing is not null)
            {
                return new TrickplayEnqueueResult { Job = Copy(existing), Created = false };
            }

            var job = new TrickplayJob
            {
                Id = Guid.NewGuid(),
                ItemId = item.Id,
                ItemName = item.Name,
                LibraryId = item.LibraryId,
                LibraryName = item.LibraryName,
                Operation = TrickplayJobOperation.GenerateMissing,
                Status = TrickplayJobStatus.Queued,
                QueuedAtUtc = DateTime.UtcNow
            };
            jobs.Add(job);
            PruneLocked(jobs);
            await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            return new TrickplayEnqueueResult { Job = Copy(job), Created = true };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates durable jobs for a bounded set and deduplicates active items.</summary>
    internal async Task<(int QueuedItems, int AlreadyActiveItems)> EnqueueManyAsync(
        IReadOnlyCollection<TrickplayItemSummary> items,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var activeItemIds = jobs
                .Where(job => job.Status is TrickplayJobStatus.Queued or TrickplayJobStatus.Generating)
                .Select(job => job.ItemId)
                .ToHashSet();
            var queuedItems = 0;
            var alreadyActiveItems = 0;
            foreach (var item in items.GroupBy(value => value.Id).Select(group => group.First()))
            {
                if (!activeItemIds.Add(item.Id))
                {
                    alreadyActiveItems++;
                    continue;
                }

                jobs.Add(new TrickplayJob
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemName = item.Name,
                    LibraryId = item.LibraryId,
                    LibraryName = item.LibraryName,
                    Operation = TrickplayJobOperation.GenerateMissing,
                    Status = TrickplayJobStatus.Queued,
                    QueuedAtUtc = DateTime.UtcNow
                });
                queuedItems++;
            }

            PruneLocked(jobs);
            await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            return (queuedItems, alreadyActiveItems);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Returns a bounded page of newest-first jobs and aggregate counts.</summary>
    public async Task<TrickplayJobPage> GetJobsAsync(
        int startIndex,
        int limit,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var countBeforePrune = jobs.Count;
            PruneLocked(jobs);
            if (jobs.Count != countBeforePrune)
            {
                await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            }

            var ordered = jobs
                .Where(job => !IsTerminal(job))
                .OrderBy(job => job.Status == TrickplayJobStatus.Generating ? 0 : 1)
                .ThenBy(job => job.QueuedAtUtc)
                .Concat(jobs
                    .Where(IsTerminal)
                    .OrderByDescending(job => job.FinishedAtUtc ?? job.QueuedAtUtc))
                .ToArray();
            var boundedStart = Math.Max(0, startIndex);
            var boundedLimit = Math.Clamp(limit, 1, 500);
            return new TrickplayJobPage
            {
                Items = ordered.Skip(boundedStart).Take(boundedLimit).Select(Copy).ToArray(),
                TotalCount = ordered.Length,
                StartIndex = boundedStart,
                PageSize = boundedLimit,
                ActiveJobs = ordered
                    .Where(job => job.Status is TrickplayJobStatus.Queued or TrickplayJobStatus.Generating)
                    .GroupBy(job => job.ItemId)
                    .Select(group => Copy(group.First()))
                    .ToArray(),
                Counts = new TrickplayJobCounts
                {
                    Queued = ordered.Count(job => job.Status == TrickplayJobStatus.Queued),
                    Generating = ordered.Count(job => job.Status == TrickplayJobStatus.Generating),
                    Completed = ordered.Count(job => job.Status == TrickplayJobStatus.Completed),
                    Failed = ordered.Count(job => job.Status == TrickplayJobStatus.Failed),
                    Cancelled = ordered.Count(job => job.Status == TrickplayJobStatus.Cancelled)
                }
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Returns one job, or <see langword="null"/> when it does not exist.</summary>
    public async Task<TrickplayJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var job = jobs.FirstOrDefault(value => value.Id == jobId);
            return job is null ? null : Copy(job);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Claims the oldest queued job for the single worker.</summary>
    public async Task<TrickplayJob?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var job = jobs
                .Where(value => value.Status == TrickplayJobStatus.Queued)
                .OrderBy(value => value.QueuedAtUtc)
                .FirstOrDefault();
            if (job is null)
            {
                return null;
            }

            job.Status = TrickplayJobStatus.Generating;
            job.StartedAtUtc = DateTime.UtcNow;
            job.FinishedAtUtc = null;
            job.AttemptCount++;
            job.ErrorMessage = string.Empty;
            await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            return Copy(job);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Marks a running job completed.</summary>
    public Task CompleteAsync(Guid jobId, CancellationToken cancellationToken)
    {
        return SetTerminalStateAsync(jobId, TrickplayJobStatus.Completed, string.Empty, cancellationToken);
    }

    /// <summary>Marks a running job failed with a sanitized message.</summary>
    public Task FailAsync(Guid jobId, string errorMessage, CancellationToken cancellationToken)
    {
        return SetTerminalStateAsync(jobId, TrickplayJobStatus.Failed, SanitizeError(errorMessage), cancellationToken);
    }

    /// <summary>Cancels a queued job; running and terminal jobs are unchanged.</summary>
    public async Task<bool> CancelQueuedAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var job = jobs.FirstOrDefault(value => value.Id == jobId);
            if (job?.Status != TrickplayJobStatus.Queued)
            {
                return false;
            }

            job.Status = TrickplayJobStatus.Cancelled;
            job.FinishedAtUtc = DateTime.UtcNow;
            await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Deletes retained terminal history without touching queued or generating jobs.</summary>
    public async Task<int> ClearHistoryAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var removed = jobs.RemoveAll(IsTerminal);
            if (removed > 0)
            {
                await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Returns interrupted running jobs to the queue after a server restart.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var interrupted = jobs.Where(job => job.Status == TrickplayJobStatus.Generating).ToArray();
            foreach (var job in interrupted)
            {
                job.Status = TrickplayJobStatus.Queued;
                job.StartedAtUtc = null;
                job.ErrorMessage = "The server stopped during generation; the job was returned to the queue.";
            }

            if (interrupted.Length > 0)
            {
                await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
            }

            return interrupted.Length;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SetTerminalStateAsync(
        Guid jobId,
        TrickplayJobStatus status,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var job = jobs.FirstOrDefault(value => value.Id == jobId)
                ?? throw new InvalidOperationException("The generation job no longer exists.");
            job.Status = status;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = errorMessage;
            PruneLocked(jobs);
            await SaveLockedAsync(jobs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<TrickplayJob>> LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (_jobs is not null)
        {
            return _jobs;
        }

        var path = GetStorePath();
        if (!File.Exists(path))
        {
            _jobs = [];
            return _jobs;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            _jobs = await JsonSerializer.DeserializeAsync<List<TrickplayJob>>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false) ?? [];
            return _jobs;
        }
        catch (JsonException exception)
        {
            _logger.LogError(exception, "The JellyTPM generation job store is not valid JSON.");
            throw new InvalidOperationException("The JellyTPM generation job store could not be read.", exception);
        }
    }

    private async Task SaveLockedAsync(List<TrickplayJob> jobs, CancellationToken cancellationToken)
    {
        var path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, jobs, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void PruneLocked(List<TrickplayJob> jobs)
    {
        var retentionDays = Math.Clamp(Plugin.Instance?.Configuration.JobHistoryRetentionDays ?? 30, 1, 365);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        jobs.RemoveAll(job =>
            IsTerminal(job)
            && job.FinishedAtUtc < cutoff);

        var maximumRetained = Math.Clamp(Plugin.Instance?.Configuration.MaximumRetainedJobs ?? 50, 1, 500);
        var excessHistoryIds = jobs
            .Where(IsTerminal)
            .OrderByDescending(job => job.FinishedAtUtc ?? job.QueuedAtUtc)
            .Skip(maximumRetained)
            .Select(job => job.Id)
            .ToHashSet();
        if (excessHistoryIds.Count > 0)
        {
            jobs.RemoveAll(job => excessHistoryIds.Contains(job.Id));
        }
    }

    private static bool IsTerminal(TrickplayJob job)
    {
        return job.Status is TrickplayJobStatus.Completed
            or TrickplayJobStatus.Failed
            or TrickplayJobStatus.Cancelled;
    }

    private static string SanitizeError(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Generation failed. Check the Jellyfin server log for details.";
        }

        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "Generation failed. Check the Jellyfin server log for details.";
        return firstLine.Length <= 500 ? firstLine : firstLine[..500];
    }

    private static TrickplayJob Copy(TrickplayJob job)
    {
        return new TrickplayJob
        {
            Id = job.Id,
            ItemId = job.ItemId,
            ItemName = job.ItemName,
            LibraryId = job.LibraryId,
            LibraryName = job.LibraryName,
            Operation = job.Operation,
            Status = job.Status,
            QueuedAtUtc = job.QueuedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            FinishedAtUtc = job.FinishedAtUtc,
            AttemptCount = job.AttemptCount,
            ErrorMessage = job.ErrorMessage
        };
    }

    private static string GetStorePath()
    {
        return Plugin.Instance?.GetDataFilePath(StoreFileName)
            ?? throw new InvalidOperationException("JellyTPM is not initialized.");
    }
}
