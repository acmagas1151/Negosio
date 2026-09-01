using Negosio.Domain.Enums;

namespace Negosio.Application.Branches;

/// <summary>
/// Owner/Admin are tenant-wide; every other role is bound to exactly one branch.
/// </summary>
public static class BranchRoles
{
    public static readonly IReadOnlySet<UserRole> BranchScoped = new HashSet<UserRole>
    {
        UserRole.Manager,
        UserRole.Cashier,
        UserRole.InventoryStaff,
        UserRole.KitchenStaff,
        UserRole.Viewer,
    };

    public static bool IsAllBranch(UserRole role) => role is UserRole.Owner or UserRole.Admin;
}
