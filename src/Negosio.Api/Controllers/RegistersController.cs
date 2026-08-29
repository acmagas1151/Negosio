using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Common;
using Negosio.Application.Registers;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/registers")]
public sealed class RegistersController : ControllerBase
{
    private readonly IRegisterService _registers;

    public RegistersController(IRegisterService registers)
    {
        _registers = registers;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<RegisterDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<RegisterDto>>> List(
        [FromQuery] RegisterListQuery query,
        CancellationToken cancellationToken)
        => Ok(await _registers.ListAsync(query, cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RegisterDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _registers.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.RegisterManage)]
    [ProducesResponseType(typeof(RegisterDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<RegisterDto>> Create(
        [FromBody] CreateRegisterRequest request,
        CancellationToken cancellationToken)
    {
        var created = await _registers.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RegisterManage)]
    [ProducesResponseType(typeof(RegisterDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<RegisterDto>> Update(
        Guid id,
        [FromBody] UpdateRegisterRequest request,
        CancellationToken cancellationToken)
        => Ok(await _registers.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.RegisterManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        await _registers.DeactivateAsync(id, cancellationToken);
        return NoContent();
    }
}
