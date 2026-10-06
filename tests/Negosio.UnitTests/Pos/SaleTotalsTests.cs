using FluentAssertions;
using Negosio.Application.Pos;
using Xunit;

namespace Negosio.UnitTests.Pos;

public class SaleTotalsTests
{
    private static readonly SaleLineCalculator.Line LineA = new(100m, 0m, 12m, 100m);
    private static readonly SaleLineCalculator.Line LineB = new(50m, 10m, 4m, 40m);

    [Fact]
    public void Tax_exclusive_adds_tax_on_top_of_discounted_subtotal()
    {
        var totals = SaleTotals.Compute(new[] { LineA, LineB }, pricesIncludeTax: false, deliveryCharge: 0m);

        totals.Subtotal.Should().Be(150m);
        totals.DiscountTotal.Should().Be(10m);
        totals.TaxTotal.Should().Be(16m);
        totals.SaleTotal.Should().Be(156m);
        totals.GrandTotal.Should().Be(156m);
    }

    [Fact]
    public void Tax_inclusive_does_not_add_tax_again()
    {
        var totals = SaleTotals.Compute(new[] { LineA, LineB }, pricesIncludeTax: true, deliveryCharge: 0m);

        totals.SaleTotal.Should().Be(140m);
        totals.GrandTotal.Should().Be(140m);
    }

    [Fact]
    public void Delivery_charge_is_added_to_grand_total_only()
    {
        var totals = SaleTotals.Compute(new[] { LineA, LineB }, pricesIncludeTax: false, deliveryCharge: 50m);

        totals.SaleTotal.Should().Be(156m);
        totals.GrandTotal.Should().Be(206m);
    }

    [Fact]
    public void Empty_lines_produce_zero_totals()
    {
        var totals = SaleTotals.Compute(Array.Empty<SaleLineCalculator.Line>(), pricesIncludeTax: false, deliveryCharge: 0m);

        totals.GrandTotal.Should().Be(0m);
    }
}
