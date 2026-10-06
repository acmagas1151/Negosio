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

    public RestoOrdersController(IRestoOrderService orders, IRestoSettlementService settlement)
    {
        _orders = orders;
        _settlement = settlement;
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
}
