using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Settings;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings/receipts")]
public sealed class ReceiptSettingsController : ControllerBase
{
    private readonly IReceiptSettingsService _service;

    public ReceiptSettingsController(IReceiptSettingsService service) => _service = service;

    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.SalesView)]
    [ProducesResponseType(typeof(ReceiptSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReceiptSettingsDto>> Get([FromQuery] Guid? branchId, CancellationToken ct)
        => Ok(await _service.GetAsync(branchId, ct));

    [HttpPut]
    [Authorize(Policy = AuthorizationPolicies.ReceiptSettingsManage)]
    [ProducesResponseType(typeof(ReceiptSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<ReceiptSettingsDto>> Put(
        [FromQuery] Guid? branchId, [FromBody] UpdateReceiptSettingsRequest request, CancellationToken ct)
        => Ok(await _service.UpdateAsync(branchId, request, ct));

    [HttpDelete]
    [Authorize(Policy = AuthorizationPolicies.ReceiptSettingsManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete([FromQuery] Guid branchId, CancellationToken ct)
    {
        await _service.ResetAsync(branchId, ct);
        return NoContent();
    }
}
