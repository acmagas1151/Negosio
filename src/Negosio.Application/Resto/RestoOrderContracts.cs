using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

// ---- Requests -----------------------------------------------------------------------------------

/// <summary>Opens a RestoPOS order. <see cref="TableId"/> is required for Bill-Out and must be null for Pay-as-you-order.</summary>
public sealed record OpenRestoOrderRequest(
    RestoServiceType ServiceType,
    Guid RegisterSessionId,
    Guid? BranchId,
    Guid? TableId,
    string? DisplayLabel);

/// <summary>Every structural write carries the client's last-seen order <c>RowVersion</c>; the server checks it after locking.</summary>
public sealed record RestoStructuralRequest(byte[] ExpectedRowVersion);

public sealed record AddRestoItemRequest(
    byte[] ExpectedRowVersion,
    Guid RoundId,
    Guid ProductVariantId,
    decimal Quantity,
    Guid StationId,
    IReadOnlyList<Guid> ModifierOptionIds,
    CheckoutDiscountInput? Discount,
    VoidSaleApprovalInput? Approval,
    string? KitchenNote);

public sealed record VoidRestoItemRequest(byte[] ExpectedRowVersion, string Reason, VoidSaleApprovalInput? Approval);

public sealed record CancelRestoOrderRequest(byte[] ExpectedRowVersion, string Reason, VoidSaleApprovalInput? Approval);

/// <summary>
/// Settles an order into one Sale. <see cref="SettlementRequestId"/> is the idempotency key: a retry with the
/// same key returns the original result. <see cref="RegisterSessionId"/> must be the caller's own open session
/// on the order's branch (spec R7), not the session the order was opened in.
/// </summary>
public sealed record SettleRestoOrderRequest(
    byte[] ExpectedRowVersion,
    Guid SettlementRequestId,
    Guid RegisterSessionId,
    IReadOnlyList<CheckoutPaymentInput> Payments);

public sealed record UnpaidCloseRestoOrderRequest(
    byte[] ExpectedRowVersion,
    Guid ClosureRequestId,
    string Reason,
    VoidSaleApprovalInput? Approval);

// ---- Results ------------------------------------------------------------------------------------

public sealed record RestoModifierDto(string GroupName, string OptionName, decimal PriceDelta);

public sealed record RestoItemDto(
    Guid Id,
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    string StationName,
    decimal Quantity,
    decimal UnitPrice,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal NetAmount,
    RestoKitchenStatus? KitchenStatus,
    DateTime? VoidedAtUtc,
    IReadOnlyList<RestoModifierDto> Modifiers);

public sealed record RestoRoundDto(Guid Id, int RoundNumber, RestoOrderRoundStatus Status, DateTime? ReleasedAtUtc, IReadOnlyList<RestoItemDto> Items);

/// <summary>Totals over non-voided items, computed with the same formula settlement will use.</summary>
public sealed record RestoOrderSummaryDto(decimal Subtotal, decimal DiscountTotal, decimal TaxTotal, decimal SaleTotal, decimal GrandTotal);

public sealed record RestoOrderDto(
    Guid Id,
    Guid BranchId,
    RestoServiceType ServiceType,
    RestoOrderStatus Status,
    Guid? TableId,
    string? DisplayLabel,
    Guid RegisterSessionId,
    bool PricesIncludeTax,
    byte[] RowVersion,
    IReadOnlyList<RestoRoundDto> Rounds,
    RestoOrderSummaryDto Summary,
    Guid? SaleId);

public sealed record RestoSettlementResultDto(
    Guid SaleId,
    string SaleNumber,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    decimal AmountPaid,
    decimal ChangeDue,
    bool WasExisting);
