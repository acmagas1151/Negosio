using FluentAssertions;
using Negosio.Application.Common;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Pos;

public class MoneyTests
{
    [Theory]
    [InlineData(1.005, 1.01)]   // half rounds up (away from zero)
    [InlineData(2.675, 2.68)]
    [InlineData(-1.005, -1.01)]
    [InlineData(10, 10.00)]
    public void Round_is_half_up_at_two_decimals(decimal input, decimal expected)
    {
        Money.Round(input).Should().Be(expected);
    }
}

public class SaleLineCalculatorTests
{
    [Fact]
    public void No_discount_no_tax_gives_gross_equals_net()
    {
        var line = SaleLineCalculator.Calculate(75m, 2m, DiscountType.None, 0m, 0m, pricesIncludeTax: false);

        line.Gross.Should().Be(150m);
        line.Discount.Should().Be(0m);
        line.Tax.Should().Be(0m);
        line.Net.Should().Be(150m);
    }

    [Fact]
    public void Percentage_discount_is_applied_to_the_gross()
    {
        var line = SaleLineCalculator.Calculate(100m, 2m, DiscountType.Percentage, 10m, 0m, false);

        line.Discount.Should().Be(20m);
        line.Net.Should().Be(180m);
    }

    [Fact]
    public void Fixed_discount_is_capped_at_the_gross()
    {
        var line = SaleLineCalculator.Calculate(50m, 1m, DiscountType.FixedAmount, 999m, 0m, false);

        line.Discount.Should().Be(50m);
        line.Net.Should().Be(0m);
    }

    [Fact]
    public void Exclusive_tax_is_added_on_top()
    {
        var line = SaleLineCalculator.Calculate(100m, 1m, DiscountType.None, 0m, 12m, pricesIncludeTax: false);

        line.Net.Should().Be(100m);
        line.Tax.Should().Be(12m);
    }

    [Fact]
    public void Inclusive_tax_is_carved_out_of_the_price()
    {
        var line = SaleLineCalculator.Calculate(112m, 1m, DiscountType.None, 0m, 12m, pricesIncludeTax: true);

        line.Net.Should().Be(112m);
        line.Tax.Should().Be(12m);
    }

    [Theory]
    [InlineData(DiscountType.Percentage, 150)]
    [InlineData(DiscountType.Percentage, -1)]
    [InlineData(DiscountType.FixedAmount, -5)]
    public void Invalid_discounts_throw(DiscountType type, decimal value)
    {
        var act = () => SaleLineCalculator.Calculate(100m, 1m, type, value, 0m, false);

        act.Should().Throw<BusinessRuleException>();
    }
}
