using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Settings;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public sealed class SettingsController : ControllerBase
{
    private readonly ITenantSettingsService _settings;

    public SettingsController(ITenantSettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet("tax")]
    [ProducesResponseType(typeof(TaxSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TaxSettingsDto>> GetTax(CancellationToken cancellationToken)
        => Ok(await _settings.GetTaxAsync(cancellationToken));

    [HttpPut("tax")]
    [Authorize(Policy = AuthorizationPolicies.TenantSettingsWrite)]
    [ProducesResponseType(typeof(TaxSettingsDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<TaxSettingsDto>> UpdateTax(
        [FromBody] UpdateTaxSettingsRequest request,
        CancellationToken cancellationToken)
        => Ok(await _settings.UpdateTaxAsync(request, cancellationToken));
}
