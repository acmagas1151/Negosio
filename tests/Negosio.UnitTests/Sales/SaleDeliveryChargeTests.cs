using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Sales;

public class SaleDeliveryChargeTests
{
    private static Sale MakeUncompletedSale()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());
        sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 2m, DiscountType.None, 0m, 150m, 0m, 0m, 150m, 40m);
        return sale;
    }

    [Fact]
    public void Complete_sets_delivery_charge_and_it_is_reflected_in_grand_total()
    {
        var sale = MakeUncompletedSale();

        sale.Complete(subtotal: 150m, discountTotal: 0m, taxTotal: 0m, deliveryCharge: 60m, grandTotal: 210m, amountPaid: 210m, changeDue: 0m);

        sale.DeliveryCharge.Should().Be(60m);
        sale.GrandTotal.Should().Be(210m);
    }

    [Fact]
    public void Complete_defaults_delivery_charge_to_zero_for_a_normal_sale()
    {
        var sale = MakeUncompletedSale();

        sale.Complete(subtotal: 150m, discountTotal: 0m, taxTotal: 0m, deliveryCharge: 0m, grandTotal: 150m, amountPaid: 150m, changeDue: 0m);

        sale.DeliveryCharge.Should().Be(0m);
    }
}
