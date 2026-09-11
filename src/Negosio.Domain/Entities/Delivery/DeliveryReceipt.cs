using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// One scheduled (or completed, or cancelled) delivery against a <see cref="Sale"/>. A sale may have
/// several of these over time — <see cref="SequenceNumber"/> is a sale-scoped "Delivery 1", "Delivery
/// 2" label, never a global document number. Captures snapshot details of what's assigned to it
/// (recipient, address, items) at creation time. A cancelled record is kept forever as audit history —
/// rescheduling always creates a brand-new record with the next sequence number, never reactivates
/// this one. Identified solely by its <see cref="Entity.Id"/>.
/// </summary>
public class DeliveryReceipt : Entity
{
    private readonly List<DeliveryReceiptItem> _items = new();

    private DeliveryReceipt()
    {
        RecipientName = string.Empty;
        DeliveryAddress = string.Empty;
        PreparedByNameSnapshot = string.Empty;
    }

    private DeliveryReceipt(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDeliveryDate,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? deliveryNotes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        SaleId = saleId;
        RelatedSaleNumber = relatedSaleNumber;
        SequenceNumber = sequenceNumber;
        ScheduledDeliveryDate = scheduledDeliveryDate;
        RecipientName = recipientName;
        DeliveryAddress = deliveryAddress;
        ContactNumber = contactNumber;
        DeliveryNotes = deliveryNotes;
        PreparedByUserId = preparedByUserId;
        PreparedByNameSnapshot = preparedByNameSnapshot;
        BatchRequestId = batchRequestId;
        Status = DeliveryStatus.Pending;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? SaleId { get; private set; }

    public string? RelatedSaleNumber { get; private set; }

    /// <summary>Sale-scoped label ("Delivery 1", "Delivery 2", ...) — never a global DR number.</summary>
    public int SequenceNumber { get; private set; }

    public DateOnly ScheduledDeliveryDate { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public string RecipientName { get; private set; }

    public string DeliveryAddress { get; private set; }

    public string? ContactNumber { get; private set; }

    public string? DeliveryNotes { get; private set; }

    public Guid PreparedByUserId { get; private set; }

    public string PreparedByNameSnapshot { get; private set; }

    public DateTime? DeliveredAtUtc { get; private set; }

    public Guid? DeliveredByUserId { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Client-supplied idempotency key for the batch-create call that produced this record
    /// (and every sibling created in the same batch) — null for a delivery created one at a time
    /// outside a batch. See the plan's Global Constraints for the idempotency strategy.</summary>
    public Guid? BatchRequestId { get; private set; }

    /// <summary>SQL Server `rowversion` — EF-managed optimistic concurrency token, never set by
    /// application code. Protects two competing status changes on the same delivery (mark-delivered
    /// racing cancel, or either racing itself from two tabs).</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<DeliveryReceiptItem> Items => _items.AsReadOnly();

    public static DeliveryReceipt Create(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDeliveryDate,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? deliveryNotes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId = null)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number must be at least 1.");
        }

        var trimmedRecipientName = recipientName?.Trim() ?? string.Empty;
        var trimmedDeliveryAddress = deliveryAddress?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedRecipientName))
        {
            throw new ArgumentException("Recipient name is required.", nameof(recipientName));
        }

        if (string.IsNullOrWhiteSpace(trimmedDeliveryAddress))
        {
            throw new ArgumentException("Delivery address is required.", nameof(deliveryAddress));
        }

        var trimmedContactNumber = contactNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedContactNumber))
        {
            trimmedContactNumber = null;
        }

        var trimmedDeliveryNotes = deliveryNotes?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDeliveryNotes))
        {
            trimmedDeliveryNotes = null;
        }

        var trimmedRelatedSaleNumber = relatedSaleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedRelatedSaleNumber))
        {
            trimmedRelatedSaleNumber = null;
        }

        var trimmedPreparedByNameSnapshot = preparedByNameSnapshot?.Trim() ?? string.Empty;

        return new DeliveryReceipt(
            tenantId,
            branchId,
            saleId,
            trimmedRelatedSaleNumber,
            sequenceNumber,
            scheduledDeliveryDate,
            trimmedRecipientName,
            trimmedDeliveryAddress,
            trimmedContactNumber,
            trimmedDeliveryNotes,
            preparedByUserId,
            trimmedPreparedByNameSnapshot,
            batchRequestId);
    }

    public DeliveryReceiptItem AddItem(
        Guid saleItemId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        decimal quantity,
        decimal? unitPrice)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        var item = new DeliveryReceiptItem(
            TenantId,
            Id,
            saleItemId,
            productNameSnapshot,
            variantNameSnapshot,
            quantity,
            unitPrice);
        _items.Add(item);
        return item;
    }

    /// <summary>Completes every item/quantity assigned to this delivery. <paramref name="deliveredAtUtc"/>
    /// is required, not read from the clock here — the caller captures one TimeProvider-sourced
    /// timestamp per attempt, matching <see cref="Sale.Void"/>'s established pattern.</summary>
    public void MarkDelivered(Guid deliveredByUserId, DateTime deliveredAtUtc)
    {
        if (Status != DeliveryStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending delivery can be marked delivered.");
        }

        Status = DeliveryStatus.Delivered;
        DeliveredAtUtc = deliveredAtUtc;
        DeliveredByUserId = deliveredByUserId;
        Touch();
    }

    /// <summary>Releases this delivery's quantities back to Unscheduled without deleting the record —
    /// it stays forever as audit history. Does not touch the Sale or its DeliveryCharge. Rescheduling
    /// creates a brand-new Pending record elsewhere; this method never reactivates one.</summary>
    public void Cancel(Guid cancelledByUserId, string reason, DateTime cancelledAtUtc)
    {
        if (Status != DeliveryStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending delivery can be cancelled.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        Status = DeliveryStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
        CancelledByUserId = cancelledByUserId;
        CancellationReason = trimmedReason;
        Touch();
    }
}
