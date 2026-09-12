using Negosio.Application.Sales;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

public sealed record CheckoutDiscountInput(DiscountType Type = DiscountType.None, decimal Value = 0m);

public sealed record CheckoutItemInput(
    Guid ProductVariantId,
    decimal Quantity,
    CheckoutDiscountInput? Discount,
    /// <summary>How much of this line is not taken at the counter today. Defaults to 0 — the whole
    /// line is Take-now unless the cashier explicitly marks part of it for delivery. Never negative,
    /// never more than <see cref="Quantity"/> (validated below and, as a backstop, by
    /// <c>Sale.AddItem</c>).</summary>
    decimal DeliveryRequiredQuantity = 0m);

public sealed record CheckoutPaymentInput(
    PaymentMethod Method,
    decimal? ReceivedAmount = null,
    decimal? Amount = null,
    string? ReferenceNumber = null);

public sealed record CheckoutRequest(
    Guid BranchId,
    Guid RegisterSessionId,
    Guid ClientRequestId,
    IReadOnlyList<CheckoutItemInput> Items,
    IReadOnlyList<CheckoutPaymentInput> Payments,
    /// <summary>The delivery fee for a "for delivery" sale, 0 for a normal sale. Added to the
    /// server-computed sale total to form <see cref="SaleResultDto.GrandTotal"/> — the client never
    /// sends a total, only this raw fee.</summary>
    decimal DeliveryCharge = 0m,
    /// <summary>Manager/Admin/Owner approval — only used when a Cashier without the DiscountApply
    /// grant submits a sale that carries any line discount. Reuses Void's approval shape.</summary>
    VoidSaleApprovalInput? Approval = null);

public sealed record SaleResultItemDto(
    Guid SaleItemId,
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal DeliveryRequiredQuantity);

public sealed record SaleResultDto(
    Guid SaleId,
    string SaleNumber,
    SaleStatus Status,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal DeliveryCharge,
    decimal GrandTotal,
    decimal AmountPaid,
    decimal ChangeDue,
    bool WasExistingRequest,
    /// <summary>The SaleItems this checkout created (or, on an idempotent replay, the SaleItems the
    /// original request created) — the frontend needs these ids to submit the follow-up delivery
    /// schedule batch for any line that carries a DeliveryRequiredQuantity > 0.</summary>
    IReadOnlyList<SaleResultItemDto> Items);

public interface ICheckoutService
{
    Task<SaleResultDto> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken = default);
}
