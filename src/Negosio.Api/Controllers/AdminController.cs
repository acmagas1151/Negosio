using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Api.Authorization;
using Negosio.Application.Abstractions;

namespace Negosio.Api.Controllers;

[ApiController]
[Route("api/admin")]
public sealed class AdminController : ControllerBase
{
    private readonly ICurrentUser _currentUser;

    public AdminController(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    /// <summary>RBAC smoke test: only users with the Owner role may call this.</summary>
    [Authorize(Policy = AuthorizationPolicies.OwnerOnly)]
    [HttpGet("owner-test")]
    public ActionResult<object> OwnerTest() => Ok(new
    {
        message = "You are an Owner.",
        userId = _currentUser.UserId,
        tenantId = _currentUser.TenantId,
        role = _currentUser.Role.ToString()
    });
}
