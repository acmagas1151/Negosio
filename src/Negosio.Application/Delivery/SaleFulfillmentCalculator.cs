namespace Negosio.Application.Delivery;

/// <summary>
/// Derives a <see cref="SaleFulfillmentStatus"/> from a sale's aggregate delivery figures. Priority-
/// ordered — the first matching rule wins. Pure and stateless: both <see cref="DeliveryReceiptService"/>
/// (the Sale-detail fulfillment summary) and <see cref="Reports.ReportsService"/> (the Sale fulfillment
/// report view) call this so the two views can never disagree about what a sale's status is.
/// </summary>
public static class SaleFulfillmentCalculator
{
    public static SaleFulfillmentStatus Derive(
        decimal totalDeliveryRequiredQuantity,
        decimal totalPendingQuantity,
        decimal totalDeliveredQuantity,
        decimal totalAvailableToScheduleQuantity,
        bool hasOverduePendingSchedule)
    {
        if (totalDeliveryRequiredQuantity == 0m) return SaleFulfillmentStatus.NotApplicable;
        if (totalDeliveredQuantity >= totalDeliveryRequiredQuantity) return SaleFulfillmentStatus.FullyDelivered;
        if (totalDeliveredQuantity > 0m) return SaleFulfillmentStatus.PartiallyDelivered;
        if (hasOverduePendingSchedule) return SaleFulfillmentStatus.NeedsRescheduling;
        if (totalAvailableToScheduleQuantity == 0m) return SaleFulfillmentStatus.FullyScheduled;
        if (totalPendingQuantity > 0m) return SaleFulfillmentStatus.PartiallyScheduled;
        return SaleFulfillmentStatus.Unscheduled;
    }
}
