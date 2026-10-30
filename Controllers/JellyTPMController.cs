using Jellyfin.Plugin.JellyTPM.Models;
using Jellyfin.Plugin.JellyTPM.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTPM.Controllers;

/// <summary>
/// Administrative, read-only API used by the JellyTPM dashboard.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("JellyTPM")]
public sealed class JellyTPMController : ControllerBase
{
    private readonly TrickplayInventoryService _inventoryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTPMController"/> class.
    /// </summary>
    public JellyTPMController(TrickplayInventoryService inventoryService)
    {
        _inventoryService = inventoryService;
    }

    /// <summary>Gets server-wide and per-library trickplay coverage.</summary>
    [HttpGet("Summary")]
    [ProducesResponseType<TrickplayInventorySummary>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TrickplayInventorySummary>> GetSummary(
        [FromQuery] bool refresh,
        CancellationToken cancellationToken)
    {
        return Ok(await _inventoryService
            .GetSummaryAsync(refresh, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>Gets a filtered and server-paged item inventory.</summary>
    [HttpGet("Items")]
    [ProducesResponseType<PagedResult<TrickplayItemSummary>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResult<TrickplayItemSummary>>> GetItems(
        [FromQuery] Guid? libraryId,
        [FromQuery] TrickplayInventoryStatus? status,
        [FromQuery] string? search,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 50,
        [FromQuery] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        if (search?.Length > 200)
        {
            return BadRequest("Search text cannot exceed 200 characters.");
        }

        if (startIndex < 0)
        {
            return BadRequest("Start index cannot be negative.");
        }

        return Ok(await _inventoryService
            .GetItemsAsync(
                libraryId,
                status,
                search,
                startIndex,
                limit,
                refresh,
                cancellationToken)
            .ConfigureAwait(false));
    }
}

