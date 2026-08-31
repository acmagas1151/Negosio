using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Negosio.Application.Auth;
using Negosio.Application.Staff;

namespace Negosio.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IStaffInvitationService _invitations;

    public AuthController(IAuthService authService, IStaffInvitationService invitations)
    {
        _authService = authService;
        _invitations = invitations;
    }

    /// <summary>Registers a new business: creates the tenant, its first branch and the owner account.</summary>
    [AllowAnonymous]
    [HttpPost("register")]
    [ProducesResponseType(typeof(RegisterResponse), StatusCodes.Status201Created)]
    public async Task<ActionResult<RegisterResponse>> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.RegisterAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Me), value: result);
    }

    /// <summary>Authenticates a user and returns a JWT access token plus their profile.</summary>
    [AllowAnonymous]
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _authService.LoginAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>Returns the currently authenticated user, their tenant and role.</summary>
    [Authorize]
    [HttpGet("me")]
    [ProducesResponseType(typeof(AuthUserDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthUserDto>> Me(CancellationToken cancellationToken)
    {
        var result = await _authService.GetCurrentUserAsync(cancellationToken);
        return Ok(result);
    }

    /// <summary>Preview a staff invitation before accepting it (no account required).</summary>
    [AllowAnonymous]
    [HttpGet("invitations/{token}")]
    [ProducesResponseType(typeof(InvitationPreviewDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<InvitationPreviewDto>> PreviewInvitation(string token, CancellationToken cancellationToken)
        => Ok(await _invitations.PreviewAsync(token, cancellationToken));

    /// <summary>Accept a staff invitation: set name + password, which creates the login and profile.</summary>
    [AllowAnonymous]
    [HttpPost("invitations/{token}/accept")]
    [ProducesResponseType(typeof(AcceptInvitationResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<AcceptInvitationResultDto>> AcceptInvitation(
        string token,
        [FromBody] AcceptInvitationRequest request,
        CancellationToken cancellationToken)
        => Ok(await _invitations.AcceptAsync(token, request, cancellationToken));
}
