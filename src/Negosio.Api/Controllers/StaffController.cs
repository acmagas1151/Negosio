using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Staff;

namespace Negosio.Api.Controllers;

// No class-level [Authorize] here: ASP.NET Core combines (ANDs) a class-level policy with any
// method-level one rather than letting the method override it, so the restrictive Owner/Admin-only
// StaffManage policy is applied per-action instead — List/Get use the broader StaffView, and
// SetPermissions uses StaffPermissionManage — to actually admit a Manager where intended.
[ApiController]
[Route("api/staff")]
public sealed class StaffController : ControllerBase
{
    private readonly IStaffService _staff;
    private readonly ISalesVoidPermissionService _permissions;

    public StaffController(IStaffService staff, ISalesVoidPermissionService permissions)
    {
        _staff = staff;
        _permissions = permissions;
    }

    /// <summary>The tenant's staff roster: accepted members plus still-open invitations.</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.StaffView)]
    [ProducesResponseType(typeof(IReadOnlyList<StaffMemberDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StaffMemberDto>>> List(CancellationToken cancellationToken)
        => Ok(await _staff.ListAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.StaffView)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.GetAsync(id, cancellationToken));

    [HttpPost("invitations")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(typeof(StaffInvitationResultDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<StaffInvitationResultDto>> Invite(
        [FromBody] InviteStaffRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _staff.InviteAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = result.InvitationId }, result);
    }

    [HttpPost("invitations/{id:guid}/resend")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(typeof(StaffInvitationResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffInvitationResultDto>> Resend(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.ResendInvitationAsync(id, cancellationToken));

    [HttpDelete("invitations/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        await _staff.RevokeInvitationAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPut("{id:guid}/role")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> ChangeRole(
        Guid id,
        [FromBody] ChangeStaffRoleRequest request,
        CancellationToken cancellationToken)
        => Ok(await _staff.ChangeRoleAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/branch")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> ChangeBranch(
        Guid id,
        [FromBody] ChangeStaffBranchRequest request,
        CancellationToken cancellationToken)
        => Ok(await _staff.ChangeBranchAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> Deactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.DeactivateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = AuthorizationPolicies.StaffManage)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> Reactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _staff.ReactivateAsync(id, cancellationToken));

    [HttpPut("{id:guid}/permissions")]
    [Authorize(Policy = AuthorizationPolicies.StaffPermissionManage)]
    [ProducesResponseType(typeof(StaffMemberDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<StaffMemberDto>> SetPermissions(
        Guid id,
        [FromBody] ChangeStaffPermissionsRequest request,
        CancellationToken cancellationToken)
        => Ok(await _permissions.SetAsync(id, request.SalesVoid, cancellationToken));
}
