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

    public RegisterSessionsController(IRegisterSessionService sessions)
    {
        _sessions = sessions;
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

    [HttpGet("current")]
    [ProducesResponseType(typeof(RegisterSessionDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterSessionDto>> Current(
        [FromQuery] Guid? registerId,
        [FromQuery] Guid? branchId,
        CancellationToken cancellationToken)
        => Ok(await _sessions.GetCurrentAsync(registerId, branchId, cancellationToken));
}
