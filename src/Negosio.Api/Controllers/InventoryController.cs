using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Common;
using Negosio.Application.Inventory;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/inventory")]
public sealed class InventoryController : ControllerBase
{
    private readonly IInventoryService _inventory;

    public InventoryController(IInventoryService inventory)
    {
        _inventory = inventory;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<InventoryRowDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<InventoryRowDto>>> List(
        [FromQuery] InventoryListQuery query,
        CancellationToken cancellationToken)
        => Ok(await _inventory.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(InventoryRowDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InventoryRowDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _inventory.GetAsync(id, cancellationToken));

    [HttpPost("adjustments")]
    [Authorize(Policy = AuthorizationPolicies.InventoryWrite)]
    [ProducesResponseType(typeof(InventoryRowDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InventoryRowDto>> Adjust(
        [FromBody] AdjustInventoryRequest request,
        CancellationToken cancellationToken)
        => Ok(await _inventory.AdjustAsync(request, cancellationToken));

    [HttpGet("movements")]
    [ProducesResponseType(typeof(PagedResult<StockMovementDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<StockMovementDto>>> Movements(
        [FromQuery] MovementListQuery query,
        CancellationToken cancellationToken)
        => Ok(await _inventory.ListMovementsAsync(query, cancellationToken));
}
