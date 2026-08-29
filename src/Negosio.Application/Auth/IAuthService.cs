namespace Negosio.Application.Auth;

public interface IAuthService
{
    Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default);

    Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns the profile of the currently authenticated user (for app reload / <c>GET /api/auth/me</c>).</summary>
    Task<AuthUserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}
