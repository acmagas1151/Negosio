using Microsoft.AspNetCore.Authorization;
using Negosio.Domain.Enums;
using Negosio.Infrastructure.Security;

namespace Negosio.Api.Authorization;

public static class AuthorizationPolicies
{
    public const string OwnerOnly = "OwnerOnly";

    public static AuthorizationOptions AddNegosioPolicies(this AuthorizationOptions options)
    {
        // Every endpoint requires an authenticated user unless it opts out with [AllowAnonymous].
        options.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();

        options.AddPolicy(OwnerOnly, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, nameof(UserRole.Owner)));

        return options;
    }
}
