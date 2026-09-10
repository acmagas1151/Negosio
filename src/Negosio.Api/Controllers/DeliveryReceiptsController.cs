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
    [ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeliveryReceiptDto>> Get(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.GetAsync(id, ct));
}
