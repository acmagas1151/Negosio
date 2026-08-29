using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

/// <summary>
/// Deterministic, backend-owned line math for checkout. Rounding is half-up at 2 dp
/// (<see cref="Money"/>), applied per line then summed. Tax is exclusive by default; when the tenant
/// marks prices tax-inclusive the tax figure is informational (already inside the price).
/// </summary>
public static class SaleLineCalculator
{
    public readonly record struct Line(decimal Gross, decimal Discount, decimal Tax, decimal Net);

    public static Line Calculate(
        decimal unitPrice,
        decimal quantity,
        DiscountType discountType,
        decimal discountValue,
        decimal taxRatePercent,
        bool pricesIncludeTax)
    {
        if (unitPrice < 0m)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "Unit price cannot be negative.");
        }

        if (quantity <= 0m)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Quantity must be greater than zero.");
        }

        var gross = Money.Round(unitPrice * quantity);

        var discount = discountType switch
        {
            DiscountType.None => 0m,
            DiscountType.Percentage => discountValue is < 0m or > 100m
                ? throw new BusinessRuleException(ErrorCodes.InvalidDiscount, "Percentage discount must be between 0 and 100.")
                : Money.Round(gross * discountValue / 100m),
            DiscountType.FixedAmount => discountValue < 0m
                ? throw new BusinessRuleException(ErrorCodes.InvalidDiscount, "A fixed discount cannot be negative.")
                : Math.Min(Money.Round(discountValue), gross),
            _ => throw new BusinessRuleException(ErrorCodes.InvalidDiscount, "Unknown discount type.")
        };

        var taxable = gross - discount;

        decimal tax;
        decimal net;
        if (taxRatePercent <= 0m)
        {
            tax = 0m;
            net = taxable;
        }
        else if (pricesIncludeTax)
        {
            var netOfTax = Money.Round(taxable / (1m + taxRatePercent / 100m));
            tax = taxable - netOfTax;
            net = taxable;
        }
        else
        {
            tax = Money.Round(taxable * taxRatePercent / 100m);
            net = taxable;
        }

        return new Line(gross, discount, tax, net);
    }
}
