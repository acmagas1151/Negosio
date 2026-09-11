namespace Negosio.Domain.Enums;

/// <summary>
/// Lifecycle of one delivery schedule (a <see cref="Entities.DeliveryReceipt"/>). Persisted
/// numerically; values must stay stable. Pending → Delivered or Pending → Cancelled are the only
/// transitions; both Delivered and Cancelled are final. A cancelled schedule is never reactivated —
/// its released quantities are picked up by a brand-new Pending record instead.
/// </summary>
public enum DeliveryStatus
{
    Pending = 1,
    Delivered = 2,
    Cancelled = 3
}
