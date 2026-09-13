using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// One scheduled (or completed, or cancelled) fulfillment against a <see cref="Sale"/> — a delivery the
/// business makes, or a pickup the customer collects. <see cref="Method"/> discriminates the two; the
/// table keeps its historical `DeliveryReceipts` name because renaming it would be pure migration risk
/// for no functional gain.
/// <para>A sale may have several of these per method. <see cref="SequenceNumber"/> is a per-sale,
/// per-method label ("Delivery 1", "Pickup 1"), never a global document number. A cancelled record is
/// kept forever as audit history — rescheduling or converting always creates a brand-new row and never
/// reactivates this one.</para>
/// </summary>
public class DeliveryReceipt : Entity
{
    private readonly List<DeliveryReceiptItem> _items = new();

    private DeliveryReceipt()
    {
        RecipientName = string.Empty;
        PreparedByNameSnapshot = string.Empty;
    }

    private DeliveryReceipt(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        FulfillmentMethod method,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string? deliveryAddress,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        SaleId = saleId;
        RelatedSaleNumber = relatedSaleNumber;
        Method = method;
        SequenceNumber = sequenceNumber;
        ScheduledDate = scheduledDate;
        RecipientName = recipientName;
        DeliveryAddress = deliveryAddress;
        ContactNumber = contactNumber;
        Notes = notes;
        PreparedByUserId = preparedByUserId;
        PreparedByNameSnapshot = preparedByNameSnapshot;
        BatchRequestId = batchRequestId;
        Status = FulfillmentStatus.Pending;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? SaleId { get; private set; }

    public string? RelatedSaleNumber { get; private set; }

    /// <summary>Delivery or Pickup. Never TakeNow — take-now quantity is complete at checkout and is
    /// never scheduled.</summary>
    public FulfillmentMethod Method { get; private set; }

    /// <summary>Per-sale, per-method label ("Delivery 1", "Pickup 1") — never a global number.</summary>
    public int SequenceNumber { get; private set; }

    /// <summary>The delivery date for a Delivery, the expected pickup date for a Pickup.</summary>
    public DateOnly ScheduledDate { get; private set; }

    /// <summary>Always Pending, Completed or Cancelled — never Unscheduled, which describes quantity
    /// not on any schedule rather than a row state.</summary>
    public FulfillmentStatus Status { get; private set; }

    /// <summary>Recipient for a Delivery, collecting customer for a Pickup.</summary>
    public string RecipientName { get; private set; }

    /// <summary>Required for Delivery, always null for Pickup (nothing is transported).</summary>
    public string? DeliveryAddress { get; private set; }

    public string? ContactNumber { get; private set; }

    public string? Notes { get; private set; }

    public Guid PreparedByUserId { get; private set; }

    public string PreparedByNameSnapshot { get; private set; }

    /// <summary>When the fulfillment actually succeeded — delivered for a Delivery, claimed for a
    /// Pickup. One pair of columns for both, labelled per method in the UI.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    public Guid? CompletedByUserId { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Where this row's quantities went when it was cancelled. Null unless
    /// <see cref="Status"/> is Cancelled.</summary>
    public CancellationDisposition? CancellationDisposition { get; private set; }

    /// <summary>Client-supplied idempotency key for the batch-create call that produced this row (and
    /// every sibling in the same batch) — null for a schedule created on its own.</summary>
    public Guid? BatchRequestId { get; private set; }

    /// <summary>SQL Server `rowversion` — EF-managed optimistic concurrency token, never set by
    /// application code. Protects two competing status changes on the same schedule.</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<DeliveryReceiptItem> Items => _items.AsReadOnly();

    public static DeliveryReceipt CreateDelivery(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId = null)
    {
        var trimmedAddress = deliveryAddress?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedAddress))
        {
            throw new ArgumentException("Delivery address is required.", nameof(deliveryAddress));
        }

        return Create(
            tenantId, branchId, saleId, relatedSaleNumber, FulfillmentMethod.Delivery, sequenceNumber,
            scheduledDate, recipientName, trimmedAddress, contactNumber, notes, preparedByUserId,
            preparedByNameSnapshot, batchRequestId);
    }

    public static DeliveryReceipt CreatePickup(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId = null) =>
        Create(
            tenantId, branchId, saleId, relatedSaleNumber, FulfillmentMethod.Pickup, sequenceNumber,
            scheduledDate, recipientName, deliveryAddress: null, contactNumber, notes, preparedByUserId,
            preparedByNameSnapshot, batchRequestId);

    private static DeliveryReceipt Create(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        FulfillmentMethod method,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string? deliveryAddress,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number must be at least 1.");
        }

        var trimmedRecipientName = recipientName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedRecipientName))
        {
            throw new ArgumentException("Recipient name is required.", nameof(recipientName));
        }

        return new DeliveryReceipt(
            tenantId,
            branchId,
            saleId,
            NullIfBlank(relatedSaleNumber),
            method,
            sequenceNumber,
            scheduledDate,
            trimmedRecipientName,
            NullIfBlank(deliveryAddress),
            NullIfBlank(contactNumber),
            NullIfBlank(notes),
            preparedByUserId,
            preparedByNameSnapshot?.Trim() ?? string.Empty,
            batchRequestId);
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
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
            TenantId, Id, saleItemId, productNameSnapshot, variantNameSnapshot, quantity, unitPrice);
        _items.Add(item);
        return item;
    }

    /// <summary>Completes a Delivery — the business delivered it. <paramref name="completedAtUtc"/> is
    /// supplied by the caller (one TimeProvider-sourced timestamp per attempt), matching
    /// <see cref="Sale.Void"/>'s established pattern.</summary>
    public void MarkDelivered(Guid completedByUserId, DateTime completedAtUtc)
    {
        if (Method != FulfillmentMethod.Delivery)
        {
            throw new InvalidOperationException("Only a delivery can be marked delivered. Use MarkClaimed for a pickup.");
        }

        Complete(completedByUserId, completedAtUtc, "Only a pending delivery can be marked delivered.");
    }

    /// <summary>Completes a Pickup — the customer collected it. This is the ONLY successful outcome for a
    /// pickup: never cancel a pickup the customer actually collected, and never record it as take-now.</summary>
    public void MarkClaimed(Guid completedByUserId, DateTime completedAtUtc)
    {
        if (Method != FulfillmentMethod.Pickup)
        {
            throw new InvalidOperationException("Only a pickup can be marked claimed. Use MarkDelivered for a delivery.");
        }

        Complete(completedByUserId, completedAtUtc, "Only a pending pickup can be marked claimed.");
    }

    private void Complete(Guid completedByUserId, DateTime completedAtUtc, string wrongStateMessage)
    {
        if (Status != FulfillmentStatus.Pending)
        {
            throw new InvalidOperationException(wrongStateMessage);
        }

        Status = FulfillmentStatus.Completed;
        CompletedAtUtc = completedAtUtc;
        CompletedByUserId = completedByUserId;
        Touch();
    }

    /// <summary>
    /// Cancels a pending schedule, recording where its quantities went. The row and its items are kept
    /// forever as history — quantities are "released" only in the sense that availability computation
    /// ignores cancelled rows. Any replacement schedule is a separate, brand-new row.
    /// <para><paramref name="disposition"/> must be valid for this row's <see cref="Method"/>; take-now
    /// is never a valid disposition.</para>
    /// </summary>
    public void Cancel(Guid cancelledByUserId, string reason, CancellationDisposition disposition, DateTime cancelledAtUtc)
    {
        if (Status != FulfillmentStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending schedule can be cancelled.");
        }

        var allowed = Method switch
        {
            FulfillmentMethod.Delivery =>
                disposition is Enums.CancellationDisposition.DeliverLater
                    or Enums.CancellationDisposition.ConvertToPickup
                    or Enums.CancellationDisposition.CustomerPickedUpInstead,
            FulfillmentMethod.Pickup =>
                disposition is Enums.CancellationDisposition.PickupLater
                    or Enums.CancellationDisposition.ConvertToDelivery,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidOperationException($"{disposition} is not a valid disposition for a {Method} schedule.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        Status = FulfillmentStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
        CancelledByUserId = cancelledByUserId;
        CancellationReason = trimmedReason;
        CancellationDisposition = disposition;
        Touch();
    }
}
