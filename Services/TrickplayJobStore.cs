using System.Text.Json;
using Jellyfin.Plugin.JellyTPM.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyTPM.Services;

/// <summary>Persists the generation queue and retained history in plugin-owned storage.</summary>
public sealed class TrickplayJobStore
{
    private const string StoreFileName = "generation-jobs.json";
    private const string StoreBackupFileName = "generation-jobs.backup.json";
    private const int MaximumAllowedActiveJobs = 5000;
    private const int MaximumInterruptedAttempts = 3;
    private const int MaximumJobPageSize = 50;
    private const long MaximumStoreFileSize = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly ILogger<TrickplayJobStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private JobStoreState? _state;
    private bool _primaryStoreTrusted = true;

    /// <summary>Initializes a new instance of the <see cref="TrickplayJobStore"/> class.</summary>
    public TrickplayJobStore(ILogger<TrickplayJobStore> logger)
    {
        _logger = logger;
    }

    /// <summary>Creates a job unless an active job already exists for the item.</summary>
    public async Task<TrickplayEnqueueResult> EnqueueAsync(
        TrickplayItemSummary item,
        TrickplayJobOperation operation,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var existing = loadedState.Jobs.FirstOrDefault(job =>
                job.ItemId == item.Id
                && job.Status is TrickplayJobStatus.Queued or TrickplayJobStatus.Generating);
            if (existing is not null)
            {
                return new TrickplayEnqueueResult { Job = Copy(existing), Created = false };
            }

            var activeLimit = GetActiveJobLimit();
            if (loadedState.Jobs.Count(IsActive) >= activeLimit)
            {
                throw new InvalidOperationException(
                    $"The JellyTPM active queue limit of {activeLimit} jobs has been reached.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var job = new TrickplayJob
            {
                Id = Guid.NewGuid(),
                ItemId = item.Id,
                ItemName = item.Name,
                ParentTitle = item.ParentTitle,
                SeasonName = item.SeasonName,
                LibraryId = item.LibraryId,
                LibraryName = item.LibraryName,
                Operation = operation,
                Status = TrickplayJobStatus.Queued,
                QueuedAtUtc = DateTime.UtcNow
            };
            state.Jobs.Add(job);
            PruneLocked(state.Jobs);
            SynchronizeCompletedTotalIfNeeded(state);
            await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
            return new TrickplayEnqueueResult { Job = Copy(job), Created = true };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Creates durable jobs for a bounded set and deduplicates active items.</summary>
    internal async Task<(int QueuedItems, int AlreadyActiveItems, int QueueCapacityLimitedItems, int ActiveQueueLimit)> EnqueueManyAsync(
        IReadOnlyCollection<TrickplayItemSummary> items,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var jobs = state.Jobs;
            var activeItemIds = jobs
                .Where(job => job.Status is TrickplayJobStatus.Queued or TrickplayJobStatus.Generating)
                .Select(job => job.ItemId)
                .ToHashSet();
            var queuedItems = 0;
            var alreadyActiveItems = 0;
            var queueCapacityLimitedItems = 0;
            var activeQueueLimit = GetActiveJobLimit();
            var activeJobCount = jobs.Count(IsActive);
            foreach (var item in items.GroupBy(value => value.Id).Select(group => group.First()))
            {
                if (activeItemIds.Contains(item.Id))
                {
                    alreadyActiveItems++;
                    continue;
                }

                if (activeJobCount >= activeQueueLimit)
                {
                    queueCapacityLimitedItems++;
                    continue;
                }

                activeItemIds.Add(item.Id);
                activeJobCount++;

                jobs.Add(new TrickplayJob
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemName = item.Name,
                    ParentTitle = item.ParentTitle,
                    SeasonName = item.SeasonName,
                    LibraryId = item.LibraryId,
                    LibraryName = item.LibraryName,
                    Operation = TrickplayJobOperation.GenerateMissing,
                    Status = TrickplayJobStatus.Queued,
                    QueuedAtUtc = DateTime.UtcNow
                });
                queuedItems++;
            }

            PruneLocked(jobs);
            SynchronizeCompletedTotalIfNeeded(state);
            await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
            return (queuedItems, alreadyActiveItems, queueCapacityLimitedItems, activeQueueLimit);
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
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var state = loadedState;
            if (RequiresMaintenance(loadedState))
            {
                cancellationToken.ThrowIfCancellationRequested();
                state = Copy(loadedState);
                PruneLocked(state.Jobs);
                SynchronizeCompletedTotalIfNeeded(state);
                await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
            }

            var jobs = state.Jobs;
            var ordered = jobs
                .Where(job => !IsTerminal(job))
                .OrderBy(job => job.Status == TrickplayJobStatus.Generating ? 0 : 1)
                .ThenBy(job => job.QueuedAtUtc)
                .Concat(jobs
                    .Where(IsTerminal)
                    .OrderByDescending(job => job.FinishedAtUtc ?? job.QueuedAtUtc))
                .ToArray();
            var boundedStart = Math.Max(0, startIndex);
            var boundedLimit = Math.Clamp(limit, 1, MaximumJobPageSize);
            return new TrickplayJobPage
            {
                Items = ordered.Skip(boundedStart).Take(boundedLimit).Select(Copy).ToArray(),
                TotalCount = ordered.Length,
                StartIndex = boundedStart,
                PageSize = boundedLimit,
                ActiveJobs = ordered
                    .Where(IsActive)
                    .GroupBy(job => job.ItemId)
                    .Select(group => group.First())
                    .Select(job => new ActiveTrickplayJob
                    {
                        ItemId = job.ItemId,
                        Status = job.Status
                    })
                    .Take(MaximumAllowedActiveJobs)
                    .ToArray(),
                Counts = new TrickplayJobCounts
                {
                    Queued = ordered.Count(job => job.Status == TrickplayJobStatus.Queued),
                    Generating = ordered.Count(job => job.Status == TrickplayJobStatus.Generating),
                    Completed = (Plugin.Instance?.Configuration.PreserveCompletedCount ?? true)
                        ? state.TotalCompleted
                        : ordered.Count(job => job.Status == TrickplayJobStatus.Completed),
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
            var jobs = (await LoadLockedAsync(cancellationToken).ConfigureAwait(false)).Jobs;
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
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var queuedJob = loadedState.Jobs
                .Where(value => value.Status == TrickplayJobStatus.Queued)
                .OrderBy(value => value.QueuedAtUtc)
                .FirstOrDefault();
            if (queuedJob is null)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var jobs = state.Jobs;
            var job = jobs.First(value => value.Id == queuedJob.Id);

            job.Status = TrickplayJobStatus.Generating;
            job.StartedAtUtc = DateTime.UtcNow;
            job.FinishedAtUtc = null;
            job.AttemptCount++;
            job.ErrorMessage = string.Empty;
            await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
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
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var loadedJob = loadedState.Jobs.FirstOrDefault(value => value.Id == jobId);
            if (loadedJob?.Status != TrickplayJobStatus.Queued)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var job = state.Jobs.First(value => value.Id == jobId);
            job.Status = TrickplayJobStatus.Cancelled;
            job.FinishedAtUtc = DateTime.UtcNow;
            await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Cancels every queued job; the generating job and terminal history are unchanged.</summary>
    public async Task<int> CancelAllQueuedAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!loadedState.Jobs.Any(job => job.Status == TrickplayJobStatus.Queued))
            {
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var jobs = state.Jobs;
            var finishedAtUtc = DateTime.UtcNow;
            var cancelled = 0;
            foreach (var job in jobs.Where(job => job.Status == TrickplayJobStatus.Queued))
            {
                job.Status = TrickplayJobStatus.Cancelled;
                job.FinishedAtUtc = finishedAtUtc;
                cancelled++;
            }

            if (cancelled > 0)
            {
                PruneLocked(jobs);
                SynchronizeCompletedTotalIfNeeded(state);
                await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return cancelled;
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
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!loadedState.Jobs.Any(IsTerminal)
                && !WouldSynchronizeCompletedTotal(loadedState))
            {
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var jobs = state.Jobs;
            var removed = jobs.RemoveAll(IsTerminal);
            var completedTotalChanged = SynchronizeCompletedTotalIfNeeded(state);
            if (removed > 0 || completedTotalChanged)
            {
                await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Recovers interrupted jobs after restart and fails jobs that exceed the retry limit.</summary>
    public async Task<(int Requeued, int Failed)> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var loadedInterrupted = loadedState.Jobs
                .Where(job => job.Status == TrickplayJobStatus.Generating)
                .ToArray();
            if (loadedInterrupted.Length == 0)
            {
                return (0, 0);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var jobs = state.Jobs;
            var interrupted = jobs.Where(job => job.Status == TrickplayJobStatus.Generating).ToArray();
            var requeued = 0;
            var failed = 0;
            foreach (var job in interrupted)
            {
                if (job.AttemptCount >= MaximumInterruptedAttempts)
                {
                    job.Status = TrickplayJobStatus.Failed;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.ErrorMessage = "Generation was interrupted too many times. Queue the item again to retry.";
                    failed++;
                }
                else
                {
                    job.Status = TrickplayJobStatus.Queued;
                    job.StartedAtUtc = null;
                    job.FinishedAtUtc = null;
                    job.ErrorMessage = "The server stopped during generation; the job was returned to the queue.";
                    requeued++;
                }
            }

            PruneLocked(jobs);
            SynchronizeCompletedTotalIfNeeded(state);
            await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);

            return (requeued, failed);
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
            var loadedState = await LoadLockedAsync(cancellationToken).ConfigureAwait(false);
            var loadedJob = loadedState.Jobs.FirstOrDefault(value => value.Id == jobId)
                ?? throw new InvalidOperationException("The generation job no longer exists.");
            if (loadedJob.Status == status)
            {
                return;
            }

            if (loadedJob.Status != TrickplayJobStatus.Generating)
            {
                throw new InvalidOperationException("Only a generating job can enter a terminal state.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var state = Copy(loadedState);
            var jobs = state.Jobs;
            var job = jobs.First(value => value.Id == jobId);
            job.Status = status;
            job.FinishedAtUtc = DateTime.UtcNow;
            job.ErrorMessage = errorMessage;
            if (status == TrickplayJobStatus.Completed)
            {
                state.TotalCompleted++;
            }

            PruneLocked(jobs);
            SynchronizeCompletedTotalIfNeeded(state);
            await CommitLockedAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<JobStoreState> LoadLockedAsync(CancellationToken cancellationToken)
    {
        if (_state is not null)
        {
            return _state;
        }

        var path = GetStorePath();
        var backupPath = GetBackupStorePath();
        if (!File.Exists(path))
        {
            if (File.Exists(backupPath))
            {
                try
                {
                    _state = await ReadStateAsync(backupPath, cancellationToken).ConfigureAwait(false);
                    try
                    {
                        File.Copy(backupPath, path, false);
                        _primaryStoreTrusted = true;
                    }
                    catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                    {
                        _primaryStoreTrusted = false;
                        _logger.LogError(
                            restoreException,
                            "Recovered the JellyTPM job store in memory, but could not recreate its missing primary file.");
                    }

                    _logger.LogWarning(
                        "Recovered the JellyTPM generation job store from its last-known-good backup because "
                        + "the primary file was missing.");
                    return _state;
                }
                catch (Exception backupException) when (backupException is JsonException or InvalidDataException)
                {
                    _logger.LogError(backupException, "The JellyTPM generation job-store backup is invalid.");
                    QuarantineFile(backupPath);
                }
            }

            _state = new JobStoreState();
            return _state;
        }

        try
        {
            _state = await ReadStateAsync(path, cancellationToken).ConfigureAwait(false);
            _primaryStoreTrusted = true;
            RefreshBackupFromValidatedPrimary(path);
            return _state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            _primaryStoreTrusted = false;
            _logger.LogError(exception, "The JellyTPM generation job store is invalid; attempting backup recovery.");
            if (File.Exists(backupPath))
            {
                JobStoreState? recovered = null;
                try
                {
                    recovered = await ReadStateAsync(backupPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception backupException) when (backupException is JsonException or InvalidDataException)
                {
                    _logger.LogError(backupException, "The JellyTPM generation job-store backup is also invalid.");
                    QuarantineFile(backupPath);
                }

                if (recovered is not null)
                {
                    QuarantineFile(path);
                    try
                    {
                        File.Copy(backupPath, path, true);
                        _primaryStoreTrusted = true;
                    }
                    catch (Exception restoreException) when (restoreException is IOException or UnauthorizedAccessException)
                    {
                        _primaryStoreTrusted = false;
                        _logger.LogError(
                            restoreException,
                            "Recovered the JellyTPM job store in memory, but could not restore its primary file.");
                    }

                    _state = recovered;
                    _logger.LogWarning("Recovered the JellyTPM generation job store from its last-known-good backup.");
                    return _state;
                }
            }

            QuarantineFile(path);
            _state = new JobStoreState();
            _logger.LogError(
                "JellyTPM started with an empty generation queue because no valid job-store backup was available. "
                + "Corrupt files were preserved for inspection.");
            return _state;
        }
    }

    private async Task<JobStoreState> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        var fileLength = new FileInfo(path).Length;
        if (fileLength > MaximumStoreFileSize)
        {
            throw new InvalidDataException(
                $"The JellyTPM generation job store exceeds the {MaximumStoreFileSize / (1024 * 1024)} MB safety limit.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JobStoreState state;
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var legacyJobs = document.RootElement.Deserialize<List<TrickplayJob>>(SerializerOptions) ?? [];
            state = new JobStoreState
            {
                Jobs = legacyJobs,
                TotalCompleted = legacyJobs.Count(job => job is not null && job.Status == TrickplayJobStatus.Completed)
            };
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            state = document.RootElement.Deserialize<JobStoreState>(SerializerOptions)
                ?? throw new InvalidDataException("The JellyTPM generation job store contains no state object.");
        }
        else
        {
            throw new InvalidDataException("The JellyTPM generation job store must contain an object or legacy job array.");
        }

        NormalizeLoadedState(state);
        return state;
    }

    private async Task CommitLockedAsync(JobStoreState state, CancellationToken cancellationToken)
    {
        var path = GetStorePath();
        var backupPath = GetBackupStorePath();
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
                await JsonSerializer.SerializeAsync(stream, state, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temporaryPath).Length > MaximumStoreFileSize)
            {
                throw new InvalidDataException(
                    $"The JellyTPM generation job store exceeds the {MaximumStoreFileSize / (1024 * 1024)} MB safety limit.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var refreshBackup = false;
            if (_primaryStoreTrusted && File.Exists(path))
            {
                File.Replace(temporaryPath, path, backupPath, true);
            }
            else
            {
                File.Move(temporaryPath, path, true);
                refreshBackup = true;
            }

            _primaryStoreTrusted = true;
            _state = state;
            if (refreshBackup)
            {
                RefreshBackupFromValidatedPrimary(path);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void QuarantineFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        var quarantinePath = Path.Combine(directory, $"{fileName}.corrupt-{timestamp}-{Guid.NewGuid():N}{extension}");
        try
        {
            File.Move(path, quarantinePath);
            _logger.LogWarning("Preserved an invalid JellyTPM job-store file as {QuarantineFileName}.", Path.GetFileName(quarantinePath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Unable to quarantine invalid JellyTPM job-store file {FileName}.", Path.GetFileName(path));
        }
    }

    private void RefreshBackupFromValidatedPrimary(string primaryPath)
    {
        var backupPath = GetBackupStorePath();
        try
        {
            File.Copy(primaryPath, backupPath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Unable to refresh the JellyTPM job-store backup.");
        }
    }

    private static void NormalizeLoadedState(JobStoreState state)
    {
        var jobs = state.Jobs;
        if (jobs is null || jobs.Any(job => job is null))
        {
            throw new InvalidDataException("The JellyTPM generation job store contains a null jobs value or job record.");
        }

        if (jobs.Count > MaximumAllowedActiveJobs + 500)
        {
            throw new InvalidDataException("The JellyTPM generation job store contains too many records.");
        }

        var jobIds = new HashSet<Guid>();
        foreach (var job in jobs)
        {
            if (job.Id == Guid.Empty
                || job.ItemId == Guid.Empty
                || !jobIds.Add(job.Id)
                || !Enum.IsDefined(job.Operation)
                || !Enum.IsDefined(job.Status))
            {
                throw new InvalidDataException("The JellyTPM generation job store contains an invalid job record.");
            }

            job.ItemName ??= string.Empty;
            job.ParentTitle ??= string.Empty;
            job.SeasonName ??= string.Empty;
            job.LibraryName ??= string.Empty;
            job.ErrorMessage ??= string.Empty;
            job.AttemptCount = Math.Clamp(job.AttemptCount, 0, MaximumInterruptedAttempts);
        }

        state.TotalCompleted = Math.Max(0, state.TotalCompleted);
    }

    private static bool RequiresMaintenance(JobStoreState state)
    {
        var retentionDays = Math.Clamp(Plugin.Instance?.Configuration.JobHistoryRetentionDays ?? 30, 1, 365);
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        var maximumRetained = Math.Clamp(Plugin.Instance?.Configuration.MaximumRetainedJobs ?? 50, 1, 500);
        return state.Jobs.Any(job => IsTerminal(job) && job.FinishedAtUtc < cutoff)
            || state.Jobs.Count(IsTerminal) > maximumRetained
            || WouldSynchronizeCompletedTotal(state);
    }

    private static bool WouldSynchronizeCompletedTotal(JobStoreState state)
    {
        return !(Plugin.Instance?.Configuration.PreserveCompletedCount ?? true)
            && state.TotalCompleted != state.Jobs.Count(job => job.Status == TrickplayJobStatus.Completed);
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

    private static bool IsActive(TrickplayJob job)
    {
        return job.Status is TrickplayJobStatus.Queued or TrickplayJobStatus.Generating;
    }

    private static int GetActiveJobLimit()
    {
        return Math.Clamp(Plugin.Instance?.Configuration.MaximumActiveJobs ?? 1000, 1, MaximumAllowedActiveJobs);
    }

    private static bool SynchronizeCompletedTotalIfNeeded(JobStoreState state)
    {
        if (Plugin.Instance?.Configuration.PreserveCompletedCount ?? true)
        {
            return false;
        }

        var retainedCompleted = state.Jobs.Count(job => job.Status == TrickplayJobStatus.Completed);
        if (state.TotalCompleted == retainedCompleted)
        {
            return false;
        }

        state.TotalCompleted = retainedCompleted;
        return true;
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
            ParentTitle = job.ParentTitle,
            SeasonName = job.SeasonName,
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

    private static JobStoreState Copy(JobStoreState state)
    {
        return new JobStoreState
        {
            Jobs = state.Jobs.Select(Copy).ToList(),
            TotalCompleted = state.TotalCompleted
        };
    }

    private static string GetStorePath()
    {
        return Plugin.Instance?.GetDataFilePath(StoreFileName)
            ?? throw new InvalidOperationException("JellyTPM is not initialized.");
    }

    private static string GetBackupStorePath()
    {
        return Plugin.Instance?.GetDataFilePath(StoreBackupFileName)
            ?? throw new InvalidOperationException("JellyTPM is not initialized.");
    }

    private sealed class JobStoreState
    {
        public List<TrickplayJob> Jobs { get; set; } = [];

        public int TotalCompleted { get; set; }
    }
}
