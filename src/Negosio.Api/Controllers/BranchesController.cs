using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Application.Branches;

namespace Negosio.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/branches")]
public sealed class BranchesController : ControllerBase
{
    private readonly IBranchQueryService _branches;

    public BranchesController(IBranchQueryService branches)
    {
        _branches = branches;
    }

    /// <summary>The current tenant's active branches. Read-only; for UI selectors.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<BranchDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<BranchDto>>> List(CancellationToken cancellationToken)
        => Ok(await _branches.ListAsync(includeInactive: false, cancellationToken));
}
