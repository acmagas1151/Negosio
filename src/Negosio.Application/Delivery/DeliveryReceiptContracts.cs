using Microsoft.EntityFrameworkCore.Storage;
using Negosio.Application.Sales;
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
/// directly as Completed/Claimed); it must be null for every other disposition.
/// </summary>
public sealed record CancelDeliveryRequest(
    string Reason,
    CancellationDisposition Disposition,
    /// <summary>Required for ConvertToPickup and CustomerPickedUpInstead — the pickup to create.
    /// Must be null for every other disposition.</summary>
    PickupReplacementInput? Replacement,
    /// <summary>Required for DeliverLater — the new Delivery to create, replacing the cancelled
    /// one. Must be null for every other disposition.</summary>
    DeliveryReplacementInput? RescheduledDelivery = null,
    /// <summary>Supplied only on a retry after the server returns FULFILLMENT_CANCEL_APPROVAL_REQUIRED
    /// — a Cashier without the FulfillmentCancel grant needs a verified Manager/Admin/Owner approval.
    /// Null (the common case) for Owner/Admin/Manager or a Cashier holding the grant.</summary>
    VoidSaleApprovalInput? Approval = null);

/// <summary>
/// Cancel a pending PICKUP. <see cref="Disposition"/> must be PickupLater or ConvertToDelivery.
/// <see cref="Replacement"/> is required for ConvertToDelivery; must be null for every other
/// disposition.
/// </summary>
public sealed record CancelPickupRequest(
    string Reason,
    CancellationDisposition Disposition,
    /// <summary>Required for ConvertToDelivery — the delivery to create. Must be null for every
    /// other disposition.</summary>
    DeliveryReplacementInput? Replacement,
    /// <summary>Required for PickupLater — the new Pickup to create, replacing the cancelled one.
    /// Must be null for every other disposition.</summary>
    PickupReplacementInput? RescheduledPickup = null,
    /// <summary>Supplied only on a retry after the server returns FULFILLMENT_CANCEL_APPROVAL_REQUIRED.
    /// Null (the common case) for Owner/Admin/Manager or a Cashier holding the grant.</summary>
    VoidSaleApprovalInput? Approval = null);

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
    /// <summary>The Manager/Admin/Owner who approved a Cashier's cancellation — null when the canceller
    /// acted directly (a privileged role, a Cashier with the grant, or the void cascade).</summary>
    string? ApprovedByName,
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

/// <summary>Whole-sale fulfillment status. A sale has at most one active (non-Cancelled) schedule
/// at any time (enforced by CreateScheduleAsync), so this never needs to express "partial" or
/// "awaiting both methods" — those were artifacts of the per-item/multi-schedule design this
/// replaces.</summary>
public enum SaleFulfillmentStatus
{
    /// <summary>No Delivery or Pickup was ever created for this sale — everything was Take now.</summary>
    TakeNow = 1,
    PendingDelivery = 2,
    Delivered = 3,
    PendingPickup = 4,
    Claimed = 5,
    /// <summary>The sale's most recent schedule is Cancelled with no active replacement — should not
    /// normally occur (every cancellation disposition creates one), but is the honest label for it
    /// rather than a crash if it ever does (e.g. data from before this simplification).</summary>
    CancelledOrReplaced = 6,
}

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
    /// <summary>The sale's current active schedule (Pending or Completed), or null for a Take-now
    /// sale that has never had one. At most one of Deliveries/Pickups below is the "active" one at
    /// any time — this field exists so the frontend never has to search two lists to find it.</summary>
    FulfillmentScheduleDto? ActiveSchedule,
    /// <summary>All Delivery schedules ever created for this sale, active and cancelled, newest
    /// first — the sale's full delivery history.</summary>
    IReadOnlyList<FulfillmentScheduleDto> Deliveries,
    /// <summary>Same as Deliveries, for Pickup.</summary>
    IReadOnlyList<FulfillmentScheduleDto> Pickups,
    IReadOnlyList<FulfillmentConversionDto> Conversions);

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

    /// <summary>Cancels the sale's active Pending fulfillment schedule (if any) as part of voiding the
    /// sale, with disposition <see cref="Negosio.Domain.Enums.CancellationDisposition.SaleVoided"/>. Must
    /// run inside <paramref name="transaction"/>, a transaction the caller (<c>VoidSaleService</c>) already
    /// began — this method never begins or commits one itself. A no-op when the sale has no Pending
    /// schedule.</summary>
    Task CancelActiveScheduleForVoidedSaleAsync(
        Guid saleId, string voidReason, IDbContextTransaction transaction, CancellationToken ct = default);
}
