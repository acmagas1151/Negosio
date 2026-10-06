using Negosio.Application.Common;

namespace Negosio.Application.Pos;

/// <summary>
/// Sale-level totals as sums of already-frozen per-line amounts. Shared by Retail checkout and
/// RestoPOS settlement so both produce identical totals from identical line data. No rounding pass
/// is applied at sale level: every line is already rounded per <see cref="Money"/>.
/// </summary>
public static class SaleTotals
{
    public readonly record struct Result(decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal SaleTotal, decimal GrandTotal);

    public static Result Compute(
        IEnumerable<SaleLineCalculator.Line> lines, bool pricesIncludeTax, decimal deliveryCharge)
    {
        var materialized = lines as IList<SaleLineCalculator.Line> ?? lines.ToList();

        var subtotal = Money.Round(materialized.Sum(l => l.Gross));
        var discountTotal = Money.Round(materialized.Sum(l => l.Discount));
        var taxTotal = Money.Round(materialized.Sum(l => l.Tax));
        var saleTotal = pricesIncludeTax
            ? subtotal - discountTotal
            : subtotal - discountTotal + taxTotal;
        var grandTotal = saleTotal + deliveryCharge;

        return new Result(subtotal, discountTotal, taxTotal, saleTotal, grandTotal);
    }
}
