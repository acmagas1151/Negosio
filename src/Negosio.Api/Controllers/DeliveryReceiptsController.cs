using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Delivery;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.SalesView)]
[Route("api/delivery-receipts")]
public sealed class DeliveryReceiptsController : ControllerBase
{
    private readonly IDeliveryReceiptService _deliveryReceipts;

    public DeliveryReceiptsController(IDeliveryReceiptService deliveryReceipts) => _deliveryReceipts = deliveryReceipts;

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(FulfillmentScheduleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FulfillmentScheduleDto>> Get(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.GetAsync(id, ct));

    [HttpPost("{id:guid}/deliver")]
    [ProducesResponseType(typeof(FulfillmentScheduleDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FulfillmentScheduleDto>> MarkDelivered(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.MarkDeliveredAsync(id, ct));

    // Narrower than the class-level SalesView — Owner/Admin/Manager only (see the plan's Global
    // Constraints / recommended authorization levels).
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.FulfillmentCancel)]
    [ProducesResponseType(typeof(CancellationResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CancellationResultDto>> Cancel(
        Guid id, [FromBody] CancelDeliveryRequest request, CancellationToken ct)
        => Ok(await _deliveryReceipts.CancelDeliveryAsync(id, request, ct));
}
