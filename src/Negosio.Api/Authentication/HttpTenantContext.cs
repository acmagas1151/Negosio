using System.Security.Claims;
using Negosio.Application.Abstractions;
using Negosio.Infrastructure.Security;

namespace Negosio.Api.Authentication;

/// <summary>
/// The tenant the current request operates against, read from the validated JWT <c>tenant_id</c>
/// claim. Never derived from client-supplied body/header values.
/// </summary>
public sealed class HttpTenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpTenantContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public bool HasTenant =>
        (Principal?.Identity?.IsAuthenticated ?? false)
        && Guid.TryParse(Principal.FindFirstValue(JwtTokenGenerator.TenantIdClaimType), out _);

    public Guid TenantId =>
        Guid.TryParse(Principal?.FindFirstValue(JwtTokenGenerator.TenantIdClaimType), out var id)
            ? id
            : throw new InvalidOperationException("The current request has no tenant_id claim.");
}
