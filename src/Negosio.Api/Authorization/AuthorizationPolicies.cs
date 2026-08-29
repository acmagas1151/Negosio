using Microsoft.AspNetCore.Authorization;
using Negosio.Application.Catalog;
using Negosio.Domain.Enums;
using Negosio.Infrastructure.Security;

namespace Negosio.Api.Authorization;

public static class AuthorizationPolicies
{
    public const string OwnerOnly = "OwnerOnly";

    /// <summary>Create / update / deactivate categories, products and variants.</summary>
    public const string CatalogWrite = "CatalogWrite";

    /// <summary>Post inventory adjustments.</summary>
    public const string InventoryWrite = "InventoryWrite";

    public static AuthorizationOptions AddNegosioPolicies(this AuthorizationOptions options)
    {
        // Every endpoint requires an authenticated user unless it opts out with [AllowAnonymous].
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        options.AddPolicy(OwnerOnly, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, nameof(UserRole.Owner)));

        options.AddPolicy(CatalogWrite, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(CatalogAccess.CatalogWriterRoles)));

        options.AddPolicy(InventoryWrite, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(CatalogAccess.InventoryWriterRoles)));

        return options;
    }

    private static IEnumerable<string> RoleNames(IEnumerable<UserRole> roles) =>
        roles.Select(r => r.ToString());
}
