using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

// ---- Create (single schedule) ----

public sealed record CreateDeliveryReceiptItemInput(Guid SaleItemId, decimal Quantity);

/// <summary>
/// One delivery schedule to create — used both standalone (<see cref="IDeliveryReceiptService.CreateAsync"/>,
/// e.g. the Sale-detail page's "Create delivery" action, or "Schedule again" after a cancellation) and as
/// one entry of <see cref="CreateDeliveryReceiptBatchRequest.Schedules"/> (checkout's initial batch).
/// </summary>
public sealed record CreateDeliveryReceiptRequest(
    DateOnly ScheduledDeliveryDate,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    IReadOnlyList<CreateDeliveryReceiptItemInput> Items);

// ---- Create (batch, at checkout) ----

/// <summary>
/// Creates every initial delivery schedule for a just-completed sale in one transaction.
/// <see cref="BatchRequestId"/> is a client-generated idempotency key: retrying the same id after a
/// network failure returns the original batch's rows rather than creating duplicates (see the plan's
/// Global Constraints — same idiom as <c>CheckoutRequest.ClientRequestId</c>).
/// </summary>
public sealed record CreateDeliveryReceiptBatchRequest(
    Guid BatchRequestId,
    IReadOnlyList<CreateDeliveryReceiptRequest> Schedules);

public sealed record DeliveryReceiptBatchResultDto(
    IReadOnlyList<DeliveryReceiptDto> Created,
    /// <summary>True when this exact <see cref="CreateDeliveryReceiptBatchRequest.BatchRequestId"/> was
    /// already applied — <see cref="Created"/> is the original batch's rows, not new ones.</summary>
    bool WasExistingBatch);

// ---- Status changes ----

public sealed record CancelDeliveryReceiptRequest(string Reason);

// ---- Read ----

public sealed record DeliveryReceiptItemDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount);

public sealed record DeliveryReceiptDto(
    Guid Id,
    Guid? SaleId,
    string? RelatedSaleNumber,
    /// <summary>Sale-scoped "Delivery {N}" label — never a global document number.</summary>
    int SequenceNumber,
    DateOnly ScheduledDeliveryDate,
    DeliveryStatus Status,
    DateTime CreatedAtUtc,
    string BranchName,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    string PreparedByName,
    DateTime? DeliveredAtUtc,
    string? DeliveredByName,
    DateTime? CancelledAtUtc,
    string? CancelledByName,
    string? CancellationReason,
    IReadOnlyList<DeliveryReceiptItemDto> Items,
    /// <summary>Read live from the linked Sale (never a stored copy on this entity) — see the plan's
    /// Global Constraints. 0 for a delivery receipt with no linked sale.</summary>
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

// ---- Sale-level fulfillment view ----

public enum SaleFulfillmentStatus
{
    /// <summary>DeliveryRequiredQuantity is 0 across the whole sale — nothing was ever marked for
    /// delivery, so fulfillment tracking does not apply.</summary>
    NotApplicable = 1,
    Unscheduled = 2,
    PartiallyScheduled = 3,
    FullyScheduled = 4,
    PartiallyDelivered = 5,
    FullyDelivered = 6,
    /// <summary>At least one Pending schedule's ScheduledDeliveryDate is before business-local today.</summary>
    NeedsRescheduling = 7,
}

public sealed record SaleItemFulfillmentDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal TakeNowQuantity,
    decimal DeliveryRequiredQuantity,
    decimal PendingQuantity,
    decimal DeliveredQuantity,
    /// <summary>DeliveryRequiredQuantity − PendingQuantity − DeliveredQuantity. Never trust a
    /// frontend-computed version of this figure for a write — this is the read-side mirror of the
    /// same server-side quantity Task 9's allocation guard enforces.</summary>
    decimal AvailableToScheduleQuantity);

public sealed record DeliveryReceiptSummaryDto(
    Guid Id,
    int SequenceNumber,
    DateOnly ScheduledDeliveryDate,
    DeliveryStatus Status,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    DateTime? DeliveredAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason,
    IReadOnlyList<DeliveryReceiptItemDto> Items);

public sealed record SaleDeliverySummaryDto(
    Guid SaleId,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    IReadOnlyList<SaleItemFulfillmentDto> Items,
    IReadOnlyList<DeliveryReceiptSummaryDto> Deliveries,
    /// <summary>True when at least one SaleItem has AvailableToScheduleQuantity > 0 — gates the
    /// Sale-detail page's "Create delivery" action. When false the UI shows: "All delivery items have
    /// already been scheduled or delivered."</summary>
    bool CanCreateDelivery);

public interface IDeliveryReceiptService
{
    Task<DeliveryReceiptBatchResultDto> CreateBatchAsync(
        Guid saleId, CreateDeliveryReceiptBatchRequest request, CancellationToken ct = default);

    Task<DeliveryReceiptDto> CreateAsync(
        Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<DeliveryReceiptDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default);

    Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default);

    Task<DeliveryReceiptDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default);

    Task<DeliveryReceiptDto> CancelAsync(Guid id, CancelDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<SaleDeliverySummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default);
}
