using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Sales;

public class SaleItemModifierTests
{
    private static SaleItem NewLine()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());
        return sale.AddItem(
            Guid.NewGuid(), "Burger", null, null, null, 120m, 2m, DiscountType.None, 0m,
            240m, 0m, 0m, 240m, 50m);
    }

    [Fact]
    public void Modifiers_are_recorded_in_order_with_snapshots()
    {
        var line = NewLine();

        line.AddModifier("Add-ons", "Extra cheese", 20m);
        line.AddModifier("Spice", "Hot", 0m);

        line.Modifiers.Should().HaveCount(2);
        line.Modifiers.First().ModifierOptionNameSnapshot.Should().Be("Extra cheese");
        line.Modifiers.First().PriceDeltaSnapshot.Should().Be(20m);
        line.Modifiers.First().SortOrder.Should().Be(1);
        line.Modifiers.Last().SortOrder.Should().Be(2);
        line.Modifiers.First().SaleItemId.Should().Be(line.Id);
    }

    [Fact]
    public void A_retail_line_has_no_modifiers()
    {
        NewLine().Modifiers.Should().BeEmpty();
    }

    [Fact]
    public void A_negative_modifier_price_delta_is_rejected()
    {
        var line = NewLine();

        var act = () => line.AddModifier("Add-ons", "Discount", -5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
