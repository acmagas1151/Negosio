using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Registers;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.PosOperate)]
[Route("api/register-sessions")]
public sealed class RegisterSessionsController : ControllerBase
{
    private readonly IRegisterSessionService _sessions;
    private readonly IRegisterCashMovementService _cashMovements;
    private readonly ICashDrawerService _cashDrawer;

    public RegisterSessionsController(
        IRegisterSessionService sessions, IRegisterCashMovementService cashMovements, ICashDrawerService cashDrawer)
    {
        _sessions = sessions;
        _cashMovements = cashMovements;
        _cashDrawer = cashDrawer;
    }

    [HttpPost("open")]
    [ProducesResponseType(typeof(RegisterSessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterSessionDto>> Open(
        [FromBody] OpenRegisterSessionRequest request,
        CancellationToken cancellationToken)
        => Ok(await _sessions.OpenAsync(request, cancellationToken));

    [HttpPost("{id:guid}/close")]
    [ProducesResponseType(typeof(RegisterSessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterSessionDto>> Close(
        Guid id,
        [FromBody] CloseRegisterSessionRequest request,
        CancellationToken cancellationToken)
        => Ok(await _sessions.CloseAsync(id, request, cancellationToken));

    /// <summary>Owner/Admin override: close another user's stuck session.</summary>
    [HttpPost("{id:guid}/force-close")]
    [Authorize(Policy = AuthorizationPolicies.RegisterForceClose)]
    [ProducesResponseType(typeof(RegisterSessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterSessionDto>> ForceClose(
        Guid id,
        [FromBody] CloseRegisterSessionRequest request,
        CancellationToken cancellationToken)
        => Ok(await _sessions.ForceCloseAsync(id, request, cancellationToken));

    [HttpGet("current")]
    [ProducesResponseType(typeof(RegisterSessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterSessionDto>> Current(
        [FromQuery] Guid? registerId,
        [FromQuery] Guid? branchId,
        CancellationToken cancellationToken)
        => Ok(await _sessions.GetCurrentAsync(registerId, branchId, cancellationToken));

    [HttpPost("{id:guid}/cash-movements")]
    [ProducesResponseType(typeof(RegisterCashMovementDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<RegisterCashMovementDto>> CreateCashMovement(
        Guid id, [FromBody] CreateCashMovementRequest request, CancellationToken cancellationToken)
    {
        var created = await _cashMovements.CreateAsync(id, request, cancellationToken);
        return CreatedAtAction(nameof(ListCashMovements), new { id }, created);
    }

    [HttpGet("{id:guid}/cash-movements")]
    [ProducesResponseType(typeof(IReadOnlyList<RegisterCashMovementDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RegisterCashMovementDto>>> ListCashMovements(
        Guid id, CancellationToken cancellationToken)
        => Ok(await _cashMovements.ListAsync(id, cancellationToken));

    /// <summary>
    /// No hardware/drawer-device integration exists yet — this authorizes and audits a no-sale
    /// drawer-open request. It never claims a physical drawer was triggered.
    /// </summary>
    [HttpPost("{id:guid}/cash-drawer/open")]
    [ProducesResponseType(typeof(CashDrawerOpenDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<CashDrawerOpenDto>> OpenCashDrawer(
        Guid id, [FromBody] OpenCashDrawerRequest request, CancellationToken cancellationToken)
    {
        var created = await _cashDrawer.OpenAsync(id, request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, created);
    }
}
