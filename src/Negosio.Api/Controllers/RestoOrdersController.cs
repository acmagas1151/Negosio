using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Resto;

namespace Negosio.Api.Controllers;

/// <summary>
/// RestoPOS order lifecycle. Every POS role may attempt these operations; the service enforces the
/// per-action authorization (grant or manager approval) and the order's lifecycle rules.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.PosOperate)]
[Route("api/resto/orders")]
public sealed class RestoOrdersController : ControllerBase
{
    private readonly IRestoOrderService _orders;
    private readonly IRestoSettlementService _settlement;
    private readonly IRestoReleaseQueryService _releases;

    public RestoOrdersController(IRestoOrderService orders, IRestoSettlementService settlement, IRestoReleaseQueryService releases)
    {
        _orders = orders;
        _settlement = settlement;
        _releases = releases;
    }

    [HttpPost]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<RestoOrderDto>> Open([FromBody] OpenRestoOrderRequest request, CancellationToken cancellationToken)
    {
        var created = await _orders.OpenAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _orders.GetAsync(id, cancellationToken));

    [HttpPost("{id:guid}/rounds")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> AddRound(Guid id, [FromBody] RestoStructuralRequest request, CancellationToken cancellationToken)
        => Ok(await _orders.AddRoundAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/rounds/{roundId:guid}/release")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> ReleaseRound(
        Guid id, Guid roundId, [FromBody] RestoStructuralRequest request, CancellationToken cancellationToken)
        => Ok(await _orders.ReleaseRoundAsync(id, roundId, request, cancellationToken));

    [HttpPost("{id:guid}/items")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> AddItem(Guid id, [FromBody] AddRestoItemRequest request, CancellationToken cancellationToken)
        => Ok(await _orders.AddItemAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/items/{itemId:guid}/void")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> VoidItem(
        Guid id, Guid itemId, [FromBody] VoidRestoItemRequest request, CancellationToken cancellationToken)
        => Ok(await _orders.VoidItemAsync(id, itemId, request, cancellationToken));

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> Cancel(Guid id, [FromBody] CancelRestoOrderRequest request, CancellationToken cancellationToken)
        => Ok(await _orders.CancelAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/settle")]
    [ProducesResponseType(typeof(RestoSettlementResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoSettlementResultDto>> Settle(Guid id, [FromBody] SettleRestoOrderRequest request, CancellationToken cancellationToken)
        => Ok(await _settlement.SettleAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/unpaid-close")]
    [ProducesResponseType(typeof(RestoOrderDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoOrderDto>> UnpaidClose(Guid id, [FromBody] UnpaidCloseRestoOrderRequest request, CancellationToken cancellationToken)
        => Ok(await _settlement.UnpaidCloseAsync(id, request, cancellationToken));

    /// <summary>Settled Pay-as-you-order rounds still waiting for release. Oldest first; <c>limit</c> defaults to 50, capped at 200.</summary>
    [HttpGet("pending-releases")]
    [ProducesResponseType(typeof(RestoKeysetPageDto<PendingPayoReleaseRowDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoKeysetPageDto<PendingPayoReleaseRowDto>>> PendingReleases(
        [FromQuery] Guid? branchId, [FromQuery] Guid? afterOrderId, [FromQuery] int? limit, CancellationToken cancellationToken)
        => Ok(await _releases.ListPendingReleasesAsync(branchId, afterOrderId, limit, cancellationToken));

    /// <summary>
    /// Kitchen tickets available to the kitchen but unacknowledged longer than the configured threshold. This does not prove
    /// the kitchen received the ticket. <c>limit</c> defaults to 50, capped at 200.
    /// </summary>
    [HttpGet("unacknowledged-tickets")]
    [ProducesResponseType(typeof(RestoKeysetPageDto<UnacknowledgedTicketRowDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<RestoKeysetPageDto<UnacknowledgedTicketRowDto>>> UnacknowledgedTickets(
        [FromQuery] Guid? branchId, [FromQuery] Guid? afterItemId, [FromQuery] int? limit, CancellationToken cancellationToken)
        => Ok(await _releases.ListUnacknowledgedTicketsAsync(branchId, afterItemId, limit, cancellationToken));
}
