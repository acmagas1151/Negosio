using Negosio.Application.Sales;
using Negosio.Domain.Enums;

namespace Negosio.Application.Pos;

public sealed record CheckoutDiscountInput(DiscountType Type = DiscountType.None, decimal Value = 0m);

public sealed record CheckoutItemInput(Guid ProductVariantId, decimal Quantity, CheckoutDiscountInput? Discount);

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
    /// <summary>Manager/Admin/Owner approval — only used when a Cashier without the DiscountApply
    /// grant submits a sale that carries any line discount. Reuses Void's approval shape.</summary>
    VoidSaleApprovalInput? Approval = null);

public sealed record SaleResultDto(
    Guid SaleId,
    string SaleNumber,
    SaleStatus Status,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    decimal AmountPaid,
    decimal ChangeDue,
    bool WasExistingRequest);

public interface ICheckoutService
{
    Task<SaleResultDto> CheckoutAsync(CheckoutRequest request, CancellationToken cancellationToken = default);
}
