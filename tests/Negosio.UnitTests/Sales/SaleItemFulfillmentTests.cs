using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Sales;

public class SaleItemFulfillmentTests
{
    private static Sale MakeSale() =>
        Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void AddItem_defaults_delivery_required_quantity_to_zero()
    {
        var sale = MakeSale();

        var item = sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m);

        item.DeliveryRequiredQuantity.Should().Be(0m);
        item.TakeNowQuantity.Should().Be(10m);
    }

    [Fact]
    public void AddItem_accepts_a_partial_delivery_requirement()
    {
        var sale = MakeSale();

        var item = sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m,
            deliveryRequiredQuantity: 8m);

        item.DeliveryRequiredQuantity.Should().Be(8m);
        item.TakeNowQuantity.Should().Be(2m);
    }

    [Fact]
    public void AddItem_rejects_a_negative_delivery_required_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m,
            deliveryRequiredQuantity: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddItem_rejects_a_delivery_required_quantity_exceeding_sold_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m,
            deliveryRequiredQuantity: 10.5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddItem_splits_take_now_delivery_and_pickup()
    {
        var sale = MakeSale();

        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 2m);

        item.DeliveryRequiredQuantity.Should().Be(4m);
        item.PickupRequiredQuantity.Should().Be(2m);
        item.TakeNowQuantity.Should().Be(2m);
    }

    [Fact]
    public void AddItem_rejects_allocation_exceeding_sold_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 5m, pickupRequiredQuantity: 4m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddItem_rejects_a_negative_pickup_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            pickupRequiredQuantity: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConvertFulfillment_moves_quantity_from_delivery_to_pickup()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 6m);

        item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, 4m);

        item.DeliveryRequiredQuantity.Should().Be(2m);
        item.PickupRequiredQuantity.Should().Be(4m);
        item.TakeNowQuantity.Should().Be(2m); // unchanged — a conversion never touches take-now
    }

    [Fact]
    public void ConvertFulfillment_moves_quantity_from_pickup_to_delivery()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            pickupRequiredQuantity: 5m);

        item.ConvertFulfillment(FulfillmentMethod.Pickup, FulfillmentMethod.Delivery, 5m);

        item.PickupRequiredQuantity.Should().Be(0m);
        item.DeliveryRequiredQuantity.Should().Be(5m);
    }

    [Fact]
    public void ConvertFulfillment_rejects_more_than_the_source_method_holds()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 3m);

        var act = () => item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, 4m);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ConvertFulfillment_rejects_TakeNow_as_source_or_target()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 3m);

        var toTakeNow = () => item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.TakeNow, 1m);
        var fromTakeNow = () => item.ConvertFulfillment(FulfillmentMethod.TakeNow, FulfillmentMethod.Pickup, 1m);

        toTakeNow.Should().Throw<InvalidOperationException>();
        fromTakeNow.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ConvertFulfillment_rejects_a_non_positive_quantity()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 3m);

        var act = () => item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
