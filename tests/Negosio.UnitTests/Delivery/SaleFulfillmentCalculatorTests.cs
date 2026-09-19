using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Delivery;

/// <summary>
/// Direct unit coverage for <see cref="SaleFulfillmentCalculator.Derive"/> — the single pure function
/// that both the Sale-detail summary and the combined fulfillment report call, so the two views can
/// never disagree (see its own doc comment). Since a sale has at most one active (non-Cancelled)
/// schedule at a time (Task 2's guarantee), the function is now a direct state read off that one
/// schedule (or its absence) — one Fact per branch of the six-value <see cref="SaleFulfillmentStatus"/>.
/// </summary>
public class SaleFulfillmentCalculatorTests
{
    [Fact]
    public void No_schedule_derives_TakeNow()
    {
        SaleFulfillmentCalculator.Derive(null, null).Should().Be(SaleFulfillmentStatus.TakeNow);
    }

    [Fact]
    public void Pending_delivery_derives_PendingDelivery()
    {
        SaleFulfillmentCalculator.Derive(FulfillmentMethod.Delivery, FulfillmentStatus.Pending)
            .Should().Be(SaleFulfillmentStatus.PendingDelivery);
    }

    [Fact]
    public void Completed_delivery_derives_Delivered()
    {
        SaleFulfillmentCalculator.Derive(FulfillmentMethod.Delivery, FulfillmentStatus.Completed)
            .Should().Be(SaleFulfillmentStatus.Delivered);
    }

    [Fact]
    public void Pending_pickup_derives_PendingPickup()
    {
        SaleFulfillmentCalculator.Derive(FulfillmentMethod.Pickup, FulfillmentStatus.Pending)
            .Should().Be(SaleFulfillmentStatus.PendingPickup);
    }

    [Fact]
    public void Completed_pickup_derives_Claimed()
    {
        SaleFulfillmentCalculator.Derive(FulfillmentMethod.Pickup, FulfillmentStatus.Completed)
            .Should().Be(SaleFulfillmentStatus.Claimed);
    }

    [Fact]
    public void Cancelled_schedule_derives_CancelledOrReplaced()
    {
        SaleFulfillmentCalculator.Derive(FulfillmentMethod.Delivery, FulfillmentStatus.Cancelled)
            .Should().Be(SaleFulfillmentStatus.CancelledOrReplaced);
        SaleFulfillmentCalculator.Derive(FulfillmentMethod.Pickup, FulfillmentStatus.Cancelled)
            .Should().Be(SaleFulfillmentStatus.CancelledOrReplaced);
    }
}
