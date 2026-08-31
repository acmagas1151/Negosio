using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

/// <summary>
/// Pure rules for who may grant which role. The Owner role is never in any set — ownership transfer
/// is not part of this phase. Kept separate from <see cref="StaffService"/> so it is unit-testable
/// without a database.
/// </summary>
public static class StaffRoles
{
    /// <summary>Every role a staff member can hold except Owner.</summary>
    public static readonly IReadOnlyList<UserRole> NonOwnerRoles = new[]
    {
        UserRole.Admin, UserRole.Manager, UserRole.Cashier,
        UserRole.InventoryStaff, UserRole.KitchenStaff, UserRole.Viewer
    };

    /// <summary>Roles the acting user is allowed to assign (on invite or role change).</summary>
    public static IReadOnlyCollection<UserRole> AssignableBy(UserRole actor) => actor switch
    {
        UserRole.Owner => NonOwnerRoles,
        UserRole.Admin => NonOwnerRoles.Where(r => r != UserRole.Admin).ToArray(),
        _ => Array.Empty<UserRole>()
    };

    public static bool CanAssign(UserRole actor, UserRole role) => AssignableBy(actor).Contains(role);
}
