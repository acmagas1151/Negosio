using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Branches;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/branches")]
public sealed class BranchesController : ControllerBase
{
    private readonly IBranchQueryService _query;
    private readonly IBranchManagementService _management;

    public BranchesController(IBranchQueryService query, IBranchManagementService management)
    {
        _query = query;
        _management = management;
    }

    /// <summary>Branch selector feed. Scoped to the caller's branch for branch-scoped roles.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BranchDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BranchDto>>> List(
        [FromQuery] bool includeInactive, CancellationToken cancellationToken)
        => Ok(await _query.ListAsync(includeInactive, cancellationToken));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> Get(Guid id, CancellationToken cancellationToken)
        => Ok(await _management.GetAsync(id, cancellationToken));

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<BranchDto>> Create(
        [FromBody] CreateBranchRequest request, CancellationToken cancellationToken)
    {
        var branch = await _management.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = branch.Id }, branch);
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> Update(
        Guid id, [FromBody] UpdateBranchRequest request, CancellationToken cancellationToken)
        => Ok(await _management.UpdateAsync(id, request, cancellationToken));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> Deactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _management.DeactivateAsync(id, cancellationToken));

    [HttpPost("{id:guid}/reactivate")]
    [Authorize(Policy = AuthorizationPolicies.BranchManage)]
    [ProducesResponseType(typeof(BranchDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<BranchDto>> Reactivate(Guid id, CancellationToken cancellationToken)
        => Ok(await _management.ReactivateAsync(id, cancellationToken));
}
