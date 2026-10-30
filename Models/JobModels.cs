namespace Jellyfin.Plugin.JellyTPM.Models;

/// <summary>Describes the lifecycle state of a JellyTPM generation job.</summary>
public enum TrickplayJobStatus
{
    /// <summary>The job is waiting for the single worker.</summary>
    Queued,

    /// <summary>Jellyfin is generating trickplay for the item.</summary>
    Generating,

    /// <summary>Generation completed and post-generation validation passed.</summary>
    Completed,

    /// <summary>Generation or post-generation validation failed.</summary>
    Failed,

    /// <summary>The queued job was cancelled by an administrator.</summary>
    Cancelled
}

/// <summary>Describes the requested trickplay operation.</summary>
public enum TrickplayJobOperation
{
    /// <summary>Generate missing configured resolutions without replacing valid data.</summary>
    GenerateMissing
}

/// <summary>A durable trickplay generation job exposed to administrators.</summary>
public sealed class TrickplayJob
{
    /// <summary>Gets or sets the job identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the Jellyfin video identifier.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the safe item display name captured when queued.</summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>Gets or sets the containing library identifier captured when queued.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the safe library display name captured when queued.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the requested operation.</summary>
    public TrickplayJobOperation Operation { get; set; }

    /// <summary>Gets or sets the current job state.</summary>
    public TrickplayJobStatus Status { get; set; }

    /// <summary>Gets or sets the UTC time at which the job was queued.</summary>
    public DateTime QueuedAtUtc { get; set; }

    /// <summary>Gets or sets the most recent UTC start time.</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>Gets or sets the UTC terminal-state time.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Gets or sets the number of times the worker claimed the job.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Gets or sets a sanitized administrator-facing failure message.</summary>
    public string ErrorMessage { get; set; } = string.Empty;
}

/// <summary>Result returned when an administrator requests generation.</summary>
public sealed class TrickplayEnqueueResult
{
    /// <summary>Gets or sets the durable job.</summary>
    public TrickplayJob Job { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether a new job was created.</summary>
    public bool Created { get; set; }
}

/// <summary>Counts the current durable queue and history states.</summary>
public sealed class TrickplayJobCounts
{
    /// <summary>Gets or sets the number of queued jobs.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets the number of generating jobs.</summary>
    public int Generating { get; set; }

    /// <summary>Gets or sets the number of completed jobs in retained history.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets the number of failed jobs in retained history.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the number of cancelled jobs in retained history.</summary>
    public int Cancelled { get; set; }
}

/// <summary>A bounded page of durable jobs and aggregate state counts.</summary>
public sealed class TrickplayJobPage
{
    /// <summary>Gets or sets the jobs in this page.</summary>
    public TrickplayJob[] Items { get; set; } = [];

    /// <summary>Gets or sets the total number of jobs before paging.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the zero-based index of the first returned job.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the bounded page size.</summary>
    public int PageSize { get; set; }

    /// <summary>Gets or sets every item identifier with a queued or generating job.</summary>
    public Guid[] ActiveItemIds { get; set; } = [];

    /// <summary>Gets or sets aggregate counts before paging.</summary>
    public TrickplayJobCounts Counts { get; set; } = new();
}
