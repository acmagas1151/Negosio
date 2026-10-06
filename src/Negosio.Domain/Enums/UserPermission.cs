namespace Negosio.Domain.Enums;

/// <summary>
/// A per-user operational permission override, layered on top of role-based access. Persisted
/// numerically; values must stay stable. Each value is its own distinct capability — never overload
/// one value's grant to silently cover a different action.
/// </summary>
public enum UserPermission
{
    SalesVoid = 1,
    CashDrawerOpen = 2,
    DiscountApply = 3,
    SalesReturn = 4,
    FulfillmentCancel = 5,
    CashMovement = 6,
    RestoItemVoid = 7,
    RestoOrderCancel = 8,
    RestoUnpaidClose = 9,
}
