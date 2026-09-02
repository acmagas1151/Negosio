using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

public sealed record VoidEligibilityResult(bool CanVoid, string? IneligibilityCode);

/// <summary>
/// The single source of truth for "can this sale be voided right now" — used both by
/// <see cref="VoidSaleService"/> (to reject with a typed error) and by <see cref="SaleQueryService"/>
/// (to surface `CanVoid`/`VoidIneligibilityCode` on the DTO so the frontend can hide/disable the
/// button without duplicating this ordering). Checked in exactly this order for every actor,
/// including Owner — authorization never overrides domain eligibility.
/// </summary>
public static class VoidEligibility
{
    public static VoidEligibilityResult Evaluate(Sale sale, bool sessionOpen, bool sameUtcDay, bool hasReturns)
    {
        // hasReturns is checked before the status check: recording a return always moves Status away
        // from Completed (see Sale.MarkReturned — PartiallyRefunded/Refunded), so for any sale that
        // actually has a return, the status check below would otherwise always fire first and the
        // more specific SaleHasReturns code would be unreachable.
        if (hasReturns)
        {
            return new VoidEligibilityResult(false, ErrorCodes.SaleHasReturns);
        }

        if (sale.Status != SaleStatus.Completed)
        {
            return new VoidEligibilityResult(false, ErrorCodes.SaleNotVoidable);
        }

        if (!sessionOpen)
        {
            return new VoidEligibilityResult(false, ErrorCodes.VoidSessionClosed);
        }

        if (!sameUtcDay)
        {
            return new VoidEligibilityResult(false, ErrorCodes.VoidCutoffExpired);
        }

        return new VoidEligibilityResult(true, null);
    }
}
