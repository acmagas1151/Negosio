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

    [HttpPost("{id:guid}/claim")]
    [ProducesResponseType(typeof(FulfillmentScheduleDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<FulfillmentScheduleDto>> MarkClaimed(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.MarkClaimedAsync(id, ct));

    // Same effective role set as the class-level SalesView (every POS role may attempt this) —
    // FulfillmentCancelAuthorizationResolver (service-enforced) requires Owner/Admin/Manager, a
    // direct grant, or Manager/Admin/Owner approval for a Cashier.
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.FulfillmentCancel)]
    [ProducesResponseType(typeof(CancellationResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CancellationResultDto>> Cancel(
        Guid id, [FromBody] CancelDeliveryRequest request, CancellationToken ct)
        => Ok(await _deliveryReceipts.CancelDeliveryAsync(id, request, ct));

    // Pickup is the same table and the same service as Delivery — deliberately kept in this one
    // controller (see the plan's design notes) rather than a separate PickupsController, which would
    // duplicate the branch guard and error mapping for no gain. These use absolute routes because they
    // don't share this controller's "api/delivery-receipts" prefix.

    [HttpPost("~/api/sales/{saleId:guid}/pickups")]
    [ProducesResponseType(typeof(FulfillmentScheduleDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<FulfillmentScheduleDto>> CreatePickup(
        Guid saleId, [FromBody] CreatePickupRequest request, CancellationToken ct)
    {
        var created = await _deliveryReceipts.CreatePickupAsync(saleId, request, ct);
        return Created($"/api/delivery-receipts/{created.Id}", created);
    }

    // Same policy as the delivery-side Cancel above.
    [HttpPost("~/api/pickups/{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.FulfillmentCancel)]
    [ProducesResponseType(typeof(CancellationResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<CancellationResultDto>> CancelPickup(
        Guid id, [FromBody] CancelPickupRequest request, CancellationToken ct)
        => Ok(await _deliveryReceipts.CancelPickupAsync(id, request, ct));
}
