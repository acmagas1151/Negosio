using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

/// <summary>Derives a sale's whole-sale <see cref="SaleFulfillmentStatus"/> from its current active
/// (or most recent) schedule. Pure and stateless — shared by the Sale-detail summary and the
/// Delivery report's sale-level view so the two can never disagree.</summary>
public static class SaleFulfillmentCalculator
{
    /// <param name="latestSchedule">The sale's most recent Delivery-or-Pickup schedule (by
    /// CreatedAtUtc), across both methods — or null if the sale never had one (pure Take-now).</param>
    public static SaleFulfillmentStatus Derive(FulfillmentMethod? method, FulfillmentStatus? status)
    {
        if (method is null) return SaleFulfillmentStatus.TakeNow;

        return (method, status) switch
        {
            (FulfillmentMethod.Delivery, FulfillmentStatus.Pending) => SaleFulfillmentStatus.PendingDelivery,
            (FulfillmentMethod.Delivery, FulfillmentStatus.Completed) => SaleFulfillmentStatus.Delivered,
            (FulfillmentMethod.Pickup, FulfillmentStatus.Pending) => SaleFulfillmentStatus.PendingPickup,
            (FulfillmentMethod.Pickup, FulfillmentStatus.Completed) => SaleFulfillmentStatus.Claimed,
            _ => SaleFulfillmentStatus.CancelledOrReplaced,
        };
    }
}
