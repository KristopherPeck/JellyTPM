namespace Jellyfin.Plugin.JellyTPM.Models;

/// <summary>
/// Describes the native trickplay coverage state of a Jellyfin video.
/// </summary>
public enum TrickplayInventoryStatus
{
    /// <summary>The video cannot or should not be processed.</summary>
    NotEligible,

    /// <summary>The containing library has trickplay extraction disabled.</summary>
    LibraryDisabled,

    /// <summary>No expected trickplay resolution is available.</summary>
    Missing,

    /// <summary>Some, but not all, expected trickplay resolutions are available.</summary>
    Partial,

    /// <summary>All expected trickplay resolutions are available.</summary>
    Available
}

/// <summary>
/// Describes one available trickplay resolution without exposing filesystem paths.
/// </summary>
public sealed class TrickplayResolutionSummary
{
    /// <summary>Gets or sets the thumbnail width in pixels.</summary>
    public int Width { get; set; }

    /// <summary>Gets or sets the thumbnail height in pixels.</summary>
    public int Height { get; set; }

    /// <summary>Gets or sets the interval between thumbnails in milliseconds.</summary>
    public int Interval { get; set; }

    /// <summary>Gets or sets the number of thumbnails across each tile.</summary>
    public int TileWidth { get; set; }

    /// <summary>Gets or sets the number of thumbnails down each tile.</summary>
    public int TileHeight { get; set; }

    /// <summary>Gets or sets the total number of thumbnails.</summary>
    public int ThumbnailCount { get; set; }
}

/// <summary>
/// Read-only inventory row for one Jellyfin video.
/// </summary>
public sealed class TrickplayItemSummary
{
    /// <summary>Gets or sets the Jellyfin item identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the containing library identifier.</summary>
    public Guid LibraryId { get; set; }

    /// <summary>Gets or sets the containing library display name.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets the item display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the production year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the Jellyfin item type.</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning movie or television series title.</summary>
    public string ParentTitle { get; set; } = string.Empty;

    /// <summary>Gets or sets the television season name.</summary>
    public string SeasonName { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin extra type.</summary>
    public string ExtraType { get; set; } = string.Empty;

    /// <summary>Gets or sets the runtime in ticks.</summary>
    public long? RunTimeTicks { get; set; }

    /// <summary>Gets or sets a value indicating whether trickplay extraction is enabled for the library.</summary>
    public bool LibraryEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether the item is eligible for trickplay generation.</summary>
    public bool Eligible { get; set; }

    /// <summary>Gets or sets the reason an item is not eligible.</summary>
    public string EligibilityReason { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's canonical inventory status.</summary>
    public TrickplayInventoryStatus Status { get; set; }

    /// <summary>Gets or sets the administrator-facing status label.</summary>
    public string StatusLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective thumbnail widths expected for the item.</summary>
    public int[] ExpectedWidths { get; set; } = [];

    /// <summary>Gets or sets the available trickplay resolutions.</summary>
    public TrickplayResolutionSummary[] Resolutions { get; set; } = [];
}

/// <summary>
/// Coverage totals for one Jellyfin library.
/// </summary>
public sealed class TrickplayLibrarySummary
{
    /// <summary>Gets or sets the Jellyfin library identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the library display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin collection type.</summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether trickplay extraction is enabled.</summary>
    public bool TrickplayEnabled { get; set; }

    /// <summary>Gets or sets a value indicating whether trickplay data is stored alongside media.</summary>
    public bool SaveWithMedia { get; set; }

    /// <summary>Gets or sets the total number of inventoried video items.</summary>
    public int TotalItems { get; set; }

    /// <summary>Gets or sets the number of eligible video items.</summary>
    public int EligibleItems { get; set; }

    /// <summary>Gets or sets the number of items with all expected resolutions.</summary>
    public int AvailableItems { get; set; }

    /// <summary>Gets or sets the number of items with some expected resolutions.</summary>
    public int PartialItems { get; set; }

    /// <summary>Gets or sets the number of items without expected resolutions.</summary>
    public int MissingItems { get; set; }

    /// <summary>Gets or sets the number of items excluded from generation.</summary>
    public int NotEligibleItems { get; set; }

    /// <summary>Gets or sets the number of partial and missing items.</summary>
    public int RemainingItems { get; set; }

    /// <summary>
    /// Gets or sets the percentage of eligible items that are available,
    /// or <see langword="null"/> when trickplay is disabled for the library.
    /// </summary>
    public double? CompletionPercentage { get; set; }
}

/// <summary>
/// Server-wide read-only trickplay coverage snapshot.
/// </summary>
public sealed class TrickplayInventorySummary
{
    /// <summary>Gets or sets the UTC time at which the snapshot was generated.</summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>Gets or sets the globally configured trickplay widths.</summary>
    public int[] ConfiguredWidths { get; set; } = [];

    /// <summary>Gets or sets the number of inventoried libraries.</summary>
    public int LibraryCount { get; set; }

    /// <summary>Gets or sets the number of trickplay-enabled libraries.</summary>
    public int EnabledLibraryCount { get; set; }

    /// <summary>Gets or sets the total number of inventoried video items.</summary>
    public int TotalItems { get; set; }

    /// <summary>Gets or sets the number of eligible items in enabled libraries.</summary>
    public int EligibleItems { get; set; }

    /// <summary>Gets or sets the number of items with all expected resolutions.</summary>
    public int AvailableItems { get; set; }

    /// <summary>Gets or sets the number of items with some expected resolutions.</summary>
    public int PartialItems { get; set; }

    /// <summary>Gets or sets the number of items without expected resolutions.</summary>
    public int MissingItems { get; set; }

    /// <summary>Gets or sets the number of partial and missing items.</summary>
    public int RemainingItems { get; set; }

    /// <summary>Gets or sets the overall completion percentage.</summary>
    public double CompletionPercentage { get; set; }

    /// <summary>Gets or sets coverage totals for each library.</summary>
    public TrickplayLibrarySummary[] Libraries { get; set; } = [];
}

/// <summary>
/// Bounded result page returned to the dashboard.
/// </summary>
/// <typeparam name="T">Result item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Gets or sets the items in this page.</summary>
    public T[] Items { get; set; } = [];

    /// <summary>Gets or sets the total number of matching items.</summary>
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the zero-based index of the first returned item.</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets the maximum number of items requested for this page.</summary>
    public int PageSize { get; set; }
}

