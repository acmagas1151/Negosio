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

    // ---- Phase 3: Retail POS ----

    /// <summary>Create / update / deactivate POS registers.</summary>
    public const string RegisterManage = "RegisterManage";

    /// <summary>Operate the POS: open/close own session, look up catalog, run checkout.</summary>
    public const string PosOperate = "PosOperate";

    /// <summary>View sales history and receipts.</summary>
    public const string SalesView = "SalesView";

    /// <summary>Create returns / refunds.</summary>
    public const string RefundManage = "RefundManage";

    /// <summary>Change tenant-wide settings (e.g. tax).</summary>
    public const string TenantSettingsWrite = "TenantSettingsWrite";

    // ---- Phase 4: Staff & access management ----

    /// <summary>Invite staff, assign roles, deactivate/reactivate staff.</summary>
    public const string StaffManage = "StaffManage";

    private static readonly UserRole[] OwnerAdmin = [UserRole.Owner, UserRole.Admin];
    private static readonly UserRole[] ManagementRoles = [UserRole.Owner, UserRole.Admin, UserRole.Manager];
    private static readonly UserRole[] PosRoles = [UserRole.Owner, UserRole.Admin, UserRole.Manager, UserRole.Cashier];

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

        options.AddPolicy(RegisterManage, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));

        options.AddPolicy(PosOperate, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(PosRoles)));

        options.AddPolicy(SalesView, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(PosRoles)));

        options.AddPolicy(RefundManage, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));

        options.AddPolicy(TenantSettingsWrite, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));

        options.AddPolicy(StaffManage, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));

        return options;
    }

    private static IEnumerable<string> RoleNames(IEnumerable<UserRole> roles) =>
        roles.Select(r => r.ToString());
}
