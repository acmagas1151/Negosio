namespace Negosio.Domain.Enums;

/// <summary>
/// Role assigned to a user within a tenant. Values are persisted numerically, so they must remain stable.
/// </summary>
public enum UserRole
{
    Owner = 1,
    Admin = 2,
    Manager = 3,
    Cashier = 4,
    InventoryStaff = 5,
    KitchenStaff = 6,
    Viewer = 7
}
