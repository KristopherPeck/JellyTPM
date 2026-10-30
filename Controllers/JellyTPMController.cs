using Jellyfin.Plugin.JellyTPM.Models;
using Jellyfin.Plugin.JellyTPM.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyTPM.Controllers;

/// <summary>
/// Administrative API used by the JellyTPM dashboard.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Route("JellyTPM")]
public sealed class JellyTPMController : ControllerBase
{
    private readonly TrickplayInventoryService _inventoryService;
    private readonly TrickplayQueueService _queueService;

    /// <summary>
    /// Initializes a new instance of the <see cref="JellyTPMController"/> class.
    /// </summary>
    public JellyTPMController(
        TrickplayInventoryService inventoryService,
        TrickplayQueueService queueService)
    {
        _inventoryService = inventoryService;
        _queueService = queueService;
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

    /// <summary>Queues non-destructive missing trickplay generation for one video.</summary>
    [HttpPost("Items/{itemId:guid}/Generate")]
    [ProducesResponseType<TrickplayEnqueueResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<TrickplayEnqueueResult>(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TrickplayEnqueueResult>> GenerateMissing(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _queueService.EnqueueMissingAsync(itemId, cancellationToken).ConfigureAwait(false);
            return result.Created ? Accepted(result) : Ok(result);
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return BadRequest(exception.Message);
        }
    }

    /// <summary>Gets the durable generation queue and retained history.</summary>
    [HttpGet("Jobs")]
    [ProducesResponseType<TrickplayJobPage>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<TrickplayJobPage>> GetJobs(
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        if (startIndex < 0)
        {
            return BadRequest("Start index cannot be negative.");
        }

        return Ok(await _queueService.GetJobsAsync(startIndex, limit, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Cancels a queued generation job.</summary>
    [HttpDelete("Jobs/{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _queueService.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return NotFound();
        }

        if (!await _queueService.CancelQueuedAsync(jobId, cancellationToken).ConfigureAwait(false))
        {
            return Conflict("Only queued jobs can be cancelled.");
        }

        return NoContent();
    }
}

