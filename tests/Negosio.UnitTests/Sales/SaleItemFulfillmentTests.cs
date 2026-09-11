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
}
