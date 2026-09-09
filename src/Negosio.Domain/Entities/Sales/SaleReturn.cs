using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A customer return against a completed <see cref="Sale"/>. Never modifies the original sale items;
/// records what came back, how much was refunded and whether stock was restored. Built in one
/// transaction with its <see cref="Items"/>, <see cref="Refunds"/>, the ledger movements and the
/// parent sale's status change.
/// </summary>
public class SaleReturn : Entity
{
    private readonly List<SaleReturnItem> _items = new();
    private readonly List<RefundPayment> _refunds = new();

    private SaleReturn()
    {
        ReturnNumber = string.Empty;
        Reason = string.Empty;
    }

    private SaleReturn(
        Guid tenantId, Guid saleId, Guid branchId, string returnNumber, Guid createdByUserId, string reason,
        Guid? approvedByUserId)
    {
        TenantId = tenantId;
        SaleId = saleId;
        BranchId = branchId;
        ReturnNumber = returnNumber;
        CreatedByUserId = createdByUserId;
        Reason = reason;
        ApprovedByUserId = approvedByUserId;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid BranchId { get; private set; }

    public string ReturnNumber { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    /// <summary>
    /// The Manager/Admin/Owner whose password verified this return, when the requester (a Cashier
    /// without a direct <c>SalesReturn</c> grant) needed approval — set by <c>ReturnAuthorizationResolver</c>
    /// at the same moment it verifies the approver, same shape as <see cref="Sale.ApprovedByUserId"/>.
    /// Null when the requester acted directly: a privileged role, or a Cashier with the grant.
    /// </summary>
    public Guid? ApprovedByUserId { get; private set; }

    public string Reason { get; private set; }

    public decimal TotalRefund { get; private set; }

    public IReadOnlyCollection<SaleReturnItem> Items => _items.AsReadOnly();

    public IReadOnlyCollection<RefundPayment> Refunds => _refunds.AsReadOnly();

    public static SaleReturn Begin(
        Guid tenantId, Guid saleId, Guid branchId, string returnNumber, Guid createdByUserId, string reason,
        Guid? approvedByUserId = null)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A return reason is required.", nameof(reason));
        }

        return new SaleReturn(tenantId, saleId, branchId, returnNumber, createdByUserId, reason.Trim(), approvedByUserId);
    }

    public SaleReturnItem AddItem(Guid saleItemId, Guid productVariantId, string productNameSnapshot, decimal quantity, decimal refundAmount, bool restocked)
    {
        var item = new SaleReturnItem(TenantId, Id, saleItemId, productVariantId, productNameSnapshot, quantity, refundAmount, restocked);
        _items.Add(item);
        TotalRefund += refundAmount;
        return item;
    }

    public RefundPayment AddRefund(PaymentMethod method, decimal amount, string? referenceNumber)
    {
        var refund = new RefundPayment(TenantId, Id, method, amount, referenceNumber);
        _refunds.Add(refund);
        return refund;
    }
}
