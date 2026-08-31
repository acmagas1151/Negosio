using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Staff;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.StaffManage)]
[Route("api/staff")]
public sealed class StaffController : ControllerBase
{
    private readonly IStaffService _staff;

    public StaffController(IStaffService staff)
    {
        _staff = staff;
    }

    /// <summary>The tenant's staff roster: accepted members plus still-open invitations.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<StaffMemberDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StaffMemberDto>>> List(CancellationToken cancellationToken)
        => Ok(await _staff.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.GetAsync(id, cancellationToken));

    [HttpPost("invitations")]
    [ProducesResponseType(typeof(StaffInvitationResultDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<StaffInvitationResultDto>> Invite(
        [FromBody] InviteStaffRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _staff.InviteAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.InvitationId }, result);
    }

    [HttpPost("invitations/{id:guid}/resend")]
    [ProducesResponseType(typeof(StaffInvitationResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffInvitationResultDto>> Resend(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.ResendInvitationAsync(id, cancellationToken));

    [HttpDelete("invitations/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await _staff.RevokeInvitationAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:guid}/role")]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> ChangeRole(
        Guid id,
        [FromBody] ChangeStaffRoleRequest request,
        CancellationToken cancellationToken)
        => Ok(await _staff.ChangeRoleAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> Deactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.DeactivateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> Reactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.ReactivateAsync(id, cancellationToken));
}
