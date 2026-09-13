namespace Negosio.Application.Delivery;

/// <summary>
/// Derives a sale's overall <see cref="SaleFulfillmentStatus"/> from its aggregate quantities.
/// Priority-ordered — first matching rule wins. Pure and stateless, and deliberately shared by the
/// Sale-detail summary and the combined report so the two views can never disagree.
/// </summary>
public static class SaleFulfillmentCalculator
{
    public static SaleFulfillmentStatus Derive(
        decimal soldQuantity,
        decimal takeNowQuantity,
        decimal deliveredQuantity,
        decimal claimedQuantity,
        decimal deliveryPendingQuantity,
        decimal pickupPendingQuantity,
        decimal deliveryUnscheduledQuantity,
        decimal pickupUnscheduledQuantity,
        bool hasOverduePendingSchedule)
    {
        var trackedQuantity = soldQuantity - takeNowQuantity;
        if (trackedQuantity <= 0m) return SaleFulfillmentStatus.NotApplicable;

        if (takeNowQuantity + deliveredQuantity + claimedQuantity >= soldQuantity)
        {
            return SaleFulfillmentStatus.Fulfilled;
        }

        if (hasOverduePendingSchedule) return SaleFulfillmentStatus.NeedsAttention;

        var awaitingDelivery = deliveryPendingQuantity > 0m;
        var awaitingPickup = pickupPendingQuantity > 0m;
        if (awaitingDelivery && awaitingPickup) return SaleFulfillmentStatus.AwaitingDeliveryAndPickup;
        if (awaitingDelivery) return SaleFulfillmentStatus.AwaitingDelivery;
        if (awaitingPickup) return SaleFulfillmentStatus.AwaitingPickup;

        // Nothing pending. Either some quantity has completed (partially fulfilled with the rest
        // unscheduled), or nothing has completed at all (nothing scheduled yet).
        if (deliveryUnscheduledQuantity > 0m || pickupUnscheduledQuantity > 0m)
        {
            return deliveredQuantity + claimedQuantity > 0m
                ? SaleFulfillmentStatus.PartiallyFulfilled
                : SaleFulfillmentStatus.NeedsScheduling;
        }

        return SaleFulfillmentStatus.PartiallyFulfilled;
    }
}
