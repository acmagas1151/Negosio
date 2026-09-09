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

    /// <summary>Create returns / refunds — every POS role may attempt one; ReturnAuthorizationResolver
    /// (service-enforced) then requires Owner/Admin/Manager, a direct SalesReturn grant, or approval.</summary>
    public const string RefundManage = "RefundManage";

    /// <summary>Change tenant-wide settings (e.g. tax).</summary>
    public const string TenantSettingsWrite = "TenantSettingsWrite";

    // ---- Phase 4: Staff & access management ----

    /// <summary>Invite staff, assign roles, deactivate/reactivate staff.</summary>
    public const string StaffManage = "StaffManage";

    // ---- Phase 6: Void sales & cash operations ----

    /// <summary>Read the staff roster — Owner/Admin see the whole tenant, Manager sees only their branch (service-enforced).</summary>
    public const string StaffView = "StaffView";

    /// <summary>Grant/revoke the SalesVoid permission — Owner/Admin any Cashier, Manager only their own branch's Cashiers (service-enforced).</summary>
    public const string StaffPermissionManage = "StaffPermissionManage";

    // ---- Phase 5: Branch management ----

    /// <summary>Create / update / activate / deactivate branches.</summary>
    public const string BranchManage = "BranchManage";

    /// <summary>Force-close another user's register session (administrative override).</summary>
    public const string RegisterForceClose = "RegisterForceClose";

    // ---- Reports ----

    /// <summary>View sales/business reports — Owner/Admin tenant-wide, Manager their own branch only
    /// (service-enforced via IBranchAccessResolver, same as SaleQueryService/DashboardService).</summary>
    public const string ReportsView = "ReportsView";

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
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(PosRoles)));

        options.AddPolicy(TenantSettingsWrite, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));

        options.AddPolicy(StaffManage, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));

        options.AddPolicy(StaffView, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));

        options.AddPolicy(StaffPermissionManage, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));

        options.AddPolicy(ReportsView, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));

        options.AddPolicy(BranchManage, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));

        options.AddPolicy(RegisterForceClose, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(OwnerAdmin)));

        return options;
    }

    private static IEnumerable<string> RoleNames(IEnumerable<UserRole> roles) =>
        roles.Select(r => r.ToString());
}
