using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Negosio.Application.Abstractions;
using Negosio.Domain.Enums;
using Negosio.Infrastructure.Security;

namespace Negosio.Api.Authentication;

/// <summary>
/// Reads the authenticated principal from the current HTTP request's validated JWT claims.
/// Client-provided values (e.g. a TenantId in a body) can never reach this type.
/// </summary>
public sealed class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public Guid UserId => GetGuid(JwtRegisteredClaimNames.Sub, ClaimTypes.NameIdentifier);

    public Guid TenantId => GetGuid(JwtTokenGenerator.TenantIdClaimType);

    public string Email => Principal?.FindFirstValue(JwtRegisteredClaimNames.Email)
        ?? Principal?.FindFirstValue(ClaimTypes.Email)
        ?? string.Empty;

    public UserRole Role =>
        Enum.TryParse<UserRole>(
            Principal?.FindFirstValue(JwtTokenGenerator.RoleClaimType)
            ?? Principal?.FindFirstValue(ClaimTypes.Role),
            out var role)
            ? role
            : throw new InvalidOperationException("The authenticated principal has no valid role claim.");

    private Guid GetGuid(params string[] claimTypes)
    {
        foreach (var type in claimTypes)
        {
            var value = Principal?.FindFirstValue(type);
            if (Guid.TryParse(value, out var guid))
            {
                return guid;
            }
        }

        throw new InvalidOperationException(
            $"The authenticated principal has no valid claim among: {string.Join(", ", claimTypes)}.");
    }
}
