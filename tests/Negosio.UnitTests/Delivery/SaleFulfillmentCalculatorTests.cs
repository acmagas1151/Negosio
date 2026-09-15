using FluentAssertions;
using Negosio.Application.Delivery;
using Xunit;

namespace Negosio.UnitTests.Delivery;

/// <summary>
/// Direct unit coverage for <see cref="SaleFulfillmentCalculator.Derive"/> — the single pure function
/// that both the Sale-detail summary and the combined fulfillment report call, so the two views can
/// never disagree (see its own doc comment). Before this file, only 5 of its 8 possible return values
/// (<see cref="SaleFulfillmentStatus.NotApplicable"/>, <see cref="SaleFulfillmentStatus.Fulfilled"/>,
/// <see cref="SaleFulfillmentStatus.NeedsAttention"/>, <see cref="SaleFulfillmentStatus.AwaitingDelivery"/>,
/// <see cref="SaleFulfillmentStatus.NeedsScheduling"/>) were ever exercised by any test in the suite —
/// all indirectly, through the API. <see cref="SaleFulfillmentStatus.PartiallyFulfilled"/>,
/// <see cref="SaleFulfillmentStatus.AwaitingPickup"/> and
/// <see cref="SaleFulfillmentStatus.AwaitingDeliveryAndPickup"/> had no covering test at all — this
/// file closes that gap (spec item 32: "Sale-level fulfillment status is correct") with one Fact per
/// branch of the priority-ordered rule, addressed directly rather than through an API round trip.
/// </summary>
public class SaleFulfillmentCalculatorTests
{
    private static SaleFulfillmentStatus Derive(
        decimal soldQuantity = 10m,
        decimal takeNowQuantity = 0m,
        decimal deliveredQuantity = 0m,
        decimal claimedQuantity = 0m,
        decimal deliveryPendingQuantity = 0m,
        decimal pickupPendingQuantity = 0m,
        decimal deliveryUnscheduledQuantity = 0m,
        decimal pickupUnscheduledQuantity = 0m,
        bool hasOverduePendingSchedule = false) =>
        SaleFulfillmentCalculator.Derive(
            soldQuantity, takeNowQuantity, deliveredQuantity, claimedQuantity,
            deliveryPendingQuantity, pickupPendingQuantity,
            deliveryUnscheduledQuantity, pickupUnscheduledQuantity, hasOverduePendingSchedule);

    [Fact]
    public void Everything_take_now_is_NotApplicable()
    {
        Derive(soldQuantity: 10m, takeNowQuantity: 10m).Should().Be(SaleFulfillmentStatus.NotApplicable);
    }

    [Fact]
    public void TakeNow_plus_delivered_plus_claimed_meeting_sold_quantity_is_Fulfilled()
    {
        Derive(soldQuantity: 10m, takeNowQuantity: 3m, deliveredQuantity: 4m, claimedQuantity: 3m)
            .Should().Be(SaleFulfillmentStatus.Fulfilled);
    }

    [Fact]
    public void An_overdue_pending_schedule_is_NeedsAttention_even_when_something_is_also_pending()
    {
        Derive(soldQuantity: 10m, deliveryPendingQuantity: 5m, hasOverduePendingSchedule: true)
            .Should().Be(SaleFulfillmentStatus.NeedsAttention);
    }

    [Fact]
    public void Only_delivery_pending_is_AwaitingDelivery()
    {
        Derive(soldQuantity: 10m, deliveryPendingQuantity: 6m, deliveryUnscheduledQuantity: 4m)
            .Should().Be(SaleFulfillmentStatus.AwaitingDelivery);
    }

    [Fact]
    public void Only_pickup_pending_is_AwaitingPickup()
    {
        Derive(soldQuantity: 10m, pickupPendingQuantity: 6m, pickupUnscheduledQuantity: 4m)
            .Should().Be(SaleFulfillmentStatus.AwaitingPickup);
    }

    [Fact]
    public void Delivery_and_pickup_both_pending_is_AwaitingDeliveryAndPickup()
    {
        Derive(soldQuantity: 10m, deliveryPendingQuantity: 3m, pickupPendingQuantity: 3m, takeNowQuantity: 4m)
            .Should().Be(SaleFulfillmentStatus.AwaitingDeliveryAndPickup);
    }

    [Fact]
    public void Nothing_pending_but_some_completed_and_some_still_unscheduled_is_PartiallyFulfilled()
    {
        // 4 delivered, 6 still delivery-unscheduled, nothing pending — some progress, but not done.
        Derive(soldQuantity: 10m, deliveredQuantity: 4m, deliveryUnscheduledQuantity: 6m)
            .Should().Be(SaleFulfillmentStatus.PartiallyFulfilled);
    }

    [Fact]
    public void Nothing_pending_and_nothing_completed_but_intent_is_unscheduled_is_NeedsScheduling()
    {
        Derive(soldQuantity: 10m, deliveryUnscheduledQuantity: 10m)
            .Should().Be(SaleFulfillmentStatus.NeedsScheduling);
    }

    [Fact]
    public void Nothing_pending_nothing_unscheduled_and_short_of_sold_quantity_is_PartiallyFulfilled()
    {
        // Every bucket accounted for (pending/unscheduled all zero) yet take-now + delivered + claimed
        // still falls short of sold quantity — the calculator's final fallback branch.
        Derive(soldQuantity: 10m, takeNowQuantity: 4m, deliveredQuantity: 3m)
            .Should().Be(SaleFulfillmentStatus.PartiallyFulfilled);
    }

    [Fact]
    public void Fulfilled_takes_priority_over_an_overdue_flag()
    {
        // If the sale is already fully fulfilled, a stale/incorrect overdue flag must not override it —
        // Fulfilled is checked first in the priority order.
        Derive(soldQuantity: 10m, takeNowQuantity: 10m, hasOverduePendingSchedule: true)
            .Should().Be(SaleFulfillmentStatus.NotApplicable); // trackedQuantity == 0 wins first of all
    }

    [Fact]
    public void Fulfilled_takes_priority_over_overdue_when_something_is_actually_tracked()
    {
        Derive(soldQuantity: 10m, takeNowQuantity: 3m, deliveredQuantity: 7m, hasOverduePendingSchedule: true)
            .Should().Be(SaleFulfillmentStatus.Fulfilled);
    }
}
