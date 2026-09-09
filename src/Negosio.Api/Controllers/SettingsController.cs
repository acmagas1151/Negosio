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

    [HttpGet("business-info")]
    [ProducesResponseType(typeof(BusinessInfoDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BusinessInfoDto>> GetBusinessInfo(CancellationToken cancellationToken)
        => Ok(await _settings.GetBusinessInfoAsync(cancellationToken));

    [HttpPut("business-info")]
    [Authorize(Policy = AuthorizationPolicies.TenantSettingsWrite)]
    [ProducesResponseType(typeof(BusinessInfoDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BusinessInfoDto>> UpdateBusinessInfo(
        [FromBody] UpdateBusinessInfoRequest request, CancellationToken cancellationToken)
        => Ok(await _settings.UpdateBusinessInfoAsync(request, cancellationToken));
}
