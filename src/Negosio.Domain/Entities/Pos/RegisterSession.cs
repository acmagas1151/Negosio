using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A cash-drawer session on a <see cref="Register"/>. A cashier opens one with a starting cash
/// count, rings up sales against it, and closes it with a counted cash amount; the expected cash and
/// the difference are recorded for reconciliation. At most one <see cref="RegisterSessionStatus.Open"/>
/// session may exist per register (enforced by a filtered unique index).
/// </summary>
public class RegisterSession : Entity
{
    private RegisterSession()
    {
    }

    private RegisterSession(Guid tenantId, Guid branchId, Guid registerId, Guid openedByUserId, decimal openingCash)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterId = registerId;
        OpenedByUserId = openedByUserId;
        OpeningCash = openingCash;
        OpenedAtUtc = DateTime.UtcNow;
        Status = RegisterSessionStatus.Open;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterId { get; private set; }

    public Guid OpenedByUserId { get; private set; }

    public Guid? ClosedByUserId { get; private set; }

    public DateTime OpenedAtUtc { get; private set; }

    public DateTime? ClosedAtUtc { get; private set; }

    public decimal OpeningCash { get; private set; }

    public decimal? ClosingCash { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public decimal? CashDifference { get; private set; }

    public decimal? GrossCashSales { get; private set; }

    public decimal? VoidedCashSales { get; private set; }

    public decimal? RefundCashOut { get; private set; }

    public decimal? CashIn { get; private set; }

    public decimal? CashOut { get; private set; }

    public RegisterSessionStatus Status { get; private set; }

    public static RegisterSession Open(Guid tenantId, Guid branchId, Guid registerId, Guid openedByUserId, decimal openingCash)
    {
        if (openingCash < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(openingCash), "Opening cash cannot be negative.");
        }

        return new RegisterSession(tenantId, branchId, registerId, openedByUserId, openingCash);
    }

    public void Close(Guid closedByUserId, decimal closingCash, decimal expectedCash, CashReconciliationBreakdown breakdown)
    {
        if (Status == RegisterSessionStatus.Closed)
        {
            throw new InvalidOperationException("This register session is already closed.");
        }

        if (closingCash < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(closingCash), "Closing cash cannot be negative.");
        }

        ClosedByUserId = closedByUserId;
        ClosingCash = closingCash;
        ExpectedCash = expectedCash;
        CashDifference = closingCash - expectedCash;
        GrossCashSales = breakdown.GrossCashSales;
        VoidedCashSales = breakdown.VoidedCashSales;
        RefundCashOut = breakdown.RefundCashOut;
        CashIn = breakdown.CashIn;
        CashOut = breakdown.CashOut;
        ClosedAtUtc = DateTime.UtcNow;
        Status = RegisterSessionStatus.Closed;
        Touch();
    }
}
