namespace Negosio.Domain.Entities;

/// <summary>The five components that sum to a closed session's expected cash, kept separately so the
/// close-session UI can render an unambiguous breakdown instead of one opaque total.</summary>
public sealed record CashReconciliationBreakdown(
    decimal GrossCashSales, decimal VoidedCashSales, decimal RefundCashOut, decimal CashIn, decimal CashOut);
