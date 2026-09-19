using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

// ---- Create (delivery) ----

/// <summary>One delivery schedule. Covers every sale item currently earmarked for delivery, at each
/// item's full <c>DeliveryRequiredQuantity</c> — there is no per-line selection any more; a sale has at
/// most one active (non-Cancelled) delivery or pickup schedule at a time.</summary>
public sealed record CreateDeliveryReceiptRequest(
    DateOnly ScheduledDate,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? Notes);

// ---- Create (pickup) ----

/// <summary>One pickup schedule. No address — a pickup transports nothing. Covers every sale item
/// currently earmarked for pickup, at each item's full <c>PickupRequiredQuantity</c>.</summary>
public sealed record CreatePickupRequest(
    DateOnly ScheduledDate,
    string RecipientName,
    string? ContactNumber,
    string? Notes);

// ---- Cancellation with disposition ----

/// <summary>
/// Cancel a pending DELIVERY. <see cref="Disposition"/> must be DeliverLater, ConvertToPickup or
/// CustomerPickedUpInstead. <see cref="Replacement"/> is required for ConvertToPickup (the future pickup
/// to create) and for CustomerPickedUpInstead (the already-collected pickup to record, which is created
/// directly as Completed/Claimed); it must be null for DeliverLater.
/// </summary>
public sealed record CancelDeliveryRequest(
    string Reason,
    CancellationDisposition Disposition,
    PickupReplacementInput? Replacement);

/// <summary>
/// Cancel a pending PICKUP. <see cref="Disposition"/> must be PickupLater or ConvertToDelivery.
/// <see cref="Replacement"/> is required for ConvertToDelivery, null for PickupLater.
/// </summary>
public sealed record CancelPickupRequest(
    string Reason,
    CancellationDisposition Disposition,
    DeliveryReplacementInput? Replacement);

/// <summary>The pickup to create when a delivery is cancelled into one. For
/// CustomerPickedUpInstead the date is the collection date (today or earlier is fine — it already
/// happened), and the created record is Completed immediately.</summary>
public sealed record PickupReplacementInput(
    DateOnly ScheduledDate,
    string RecipientName,
    string? ContactNumber,
    string? Notes);

public sealed record DeliveryReplacementInput(
    DateOnly ScheduledDate,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? Notes);

/// <summary>A cancellation that creates a replacement returns both records, so the caller can show what
/// happened without a second round trip.</summary>
public sealed record CancellationResultDto(
    FulfillmentScheduleDto Cancelled,
    FulfillmentScheduleDto? Replacement);

// ---- Read ----

public sealed record FulfillmentItemDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount);

/// <summary>One delivery or pickup schedule, fully rendered (print-ready).</summary>
public sealed record FulfillmentScheduleDto(
    Guid Id,
    Guid? SaleId,
    string? RelatedSaleNumber,
    FulfillmentMethod Method,
    /// <summary>Per-sale, per-method label: "Delivery 1", "Pickup 1".</summary>
    int SequenceNumber,
    DateOnly ScheduledDate,
    FulfillmentStatus Status,
    DateTime CreatedAtUtc,
    string BranchName,
    string RecipientName,
    string? DeliveryAddress,
    string? ContactNumber,
    string? Notes,
    string PreparedByName,
    DateTime? CompletedAtUtc,
    string? CompletedByName,
    DateTime? CancelledAtUtc,
    string? CancelledByName,
    string? CancellationReason,
    CancellationDisposition? CancellationDisposition,
    IReadOnlyList<FulfillmentItemDto> Items,
    /// <summary>Read live from the linked Sale, never stored here. Always 0 for a pickup — a pickup
    /// never carries a delivery charge.</summary>
    decimal DeliveryCharge,
    string? HeaderText,
    string? FooterText,
    string BusinessName,
    string? BusinessAddress,
    string? BusinessContactNumber,
    string? TaxId,
    bool ShowPrices,
    bool ShowRelatedSaleNumber,
    bool ShowContactNumber,
    bool ShowSignatureFields);

// ---- Sale-level fulfillment ----

/// <summary>
/// REPLACES the shipped 7-member enum entirely. The old members (Unscheduled, PartiallyScheduled,
/// FullyScheduled, PartiallyDelivered, FullyDelivered, NeedsRescheduling) described a delivery-only
/// world and cannot express "awaiting pickup". The spec names the replacement set directly, so this
/// is a rename of the concept, not an extension of it. Consumers to update: SaleDetailPage,
/// lib/pos.ts's SALE_FULFILLMENT_STATUS_LABELS, and the delivery report's status column.
/// Note that DeliveryReportPreset's "NeedsRescheduling" is a REPORT FILTER, a different type —
/// leave it alone.
/// </summary>
public enum SaleFulfillmentStatus
{
    /// <summary>Nothing on this sale was marked for delivery or pickup — everything was taken at the
    /// counter, so there is nothing to track.</summary>
    NotApplicable = 1,
    /// <summary>TakenNow + Delivered + Claimed == sold quantity.</summary>
    Fulfilled = 2,
    PartiallyFulfilled = 3,
    AwaitingDelivery = 4,
    AwaitingPickup = 5,
    AwaitingDeliveryAndPickup = 6,
    /// <summary>Intent exists but nothing is scheduled yet.</summary>
    NeedsScheduling = 7,
    /// <summary>Something is overdue — a pending schedule whose date has passed.</summary>
    NeedsAttention = 8,
}

/// <summary>The 8-bucket per-line breakdown the Sale-detail page renders. Every bucket is derived
/// server-side; the frontend never recomputes one.</summary>
public sealed record SaleItemFulfillmentDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal TakeNowQuantity,
    decimal DeliveryUnscheduledQuantity,
    decimal DeliveryPendingQuantity,
    decimal DeliveredQuantity,
    decimal PickupUnscheduledQuantity,
    decimal PickupPendingQuantity,
    decimal ClaimedQuantity);

public sealed record FulfillmentConversionDto(
    Guid Id,
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    FulfillmentMethod FromMethod,
    FulfillmentMethod ToMethod,
    Guid? SourceRecordId,
    Guid? ReplacementRecordId,
    string Reason,
    DateTime CreatedAtUtc,
    string CreatedByName);

public sealed record SaleFulfillmentSummaryDto(
    Guid SaleId,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    IReadOnlyList<SaleItemFulfillmentDto> Items,
    IReadOnlyList<FulfillmentScheduleDto> Deliveries,
    IReadOnlyList<FulfillmentScheduleDto> Pickups,
    IReadOnlyList<FulfillmentConversionDto> Conversions,
    /// <summary>True when any line still has delivery-unscheduled quantity. Gates "Create delivery";
    /// when false the UI shows "All delivery items have already been scheduled or delivered."</summary>
    bool CanCreateDelivery,
    /// <summary>True when any line still has pickup-unscheduled quantity. Gates "Create pickup"; when
    /// false the UI shows "All pickup items have already been scheduled or claimed."</summary>
    bool CanCreatePickup);

public interface IDeliveryReceiptService
{
    Task<FulfillmentScheduleDto> CreateDeliveryAsync(Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<FulfillmentScheduleDto> CreatePickupAsync(Guid saleId, CreatePickupRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<FulfillmentScheduleDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default);
    Task<FulfillmentScheduleDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<SaleFulfillmentSummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default);

    Task<FulfillmentScheduleDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default);
    Task<FulfillmentScheduleDto> MarkClaimedAsync(Guid id, CancellationToken ct = default);

    Task<CancellationResultDto> CancelDeliveryAsync(Guid id, CancelDeliveryRequest request, CancellationToken ct = default);
    Task<CancellationResultDto> CancelPickupAsync(Guid id, CancelPickupRequest request, CancellationToken ct = default);
}
