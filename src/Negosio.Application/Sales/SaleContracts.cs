using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

public sealed record SaleItemDto(
    Guid Id,
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    string? Sku,
    string? Barcode,
    decimal UnitPrice,
    decimal Quantity,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal NetAmount,
    decimal? CostPriceSnapshot,
    decimal ReturnedQuantity);

public sealed record SalePaymentDto(
    Guid Id,
    PaymentMethod Method,
    decimal Amount,
    string? ReferenceNumber,
    decimal? ReceivedAmount,
    decimal? ChangeAmount);

public sealed record SaleSummaryDto(
    Guid Id,
    string SaleNumber,
    Guid BranchId,
    string BranchName,
    Guid CashierUserId,
    string CashierName,
    int ItemCount,
    decimal GrandTotal,
    SaleStatus Status,
    string PaymentSummary,
    DateTime CreatedAtUtc);

public sealed record SaleDetailDto(
    SaleSummaryDto Sale,
    Guid RegisterSessionId,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal AmountPaid,
    decimal ChangeDue,
    DateTime? CompletedAtUtc,
    IReadOnlyList<SaleItemDto> Items,
    IReadOnlyList<SalePaymentDto> Payments,
    IReadOnlyList<SaleReturnDto> Returns);

public sealed record SaleListQuery(
    Guid? BranchId = null,
    Guid? RegisterId = null,
    Guid? CashierUserId = null,
    SaleStatus? Status = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<SaleSummaryDto>.DefaultPageSize);

public interface ISaleQueryService
{
    Task<PagedResult<SaleSummaryDto>> ListAsync(SaleListQuery query, CancellationToken cancellationToken = default);

    Task<SaleDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);
}

// ---- Receipt ----

public sealed record ReceiptLineDto(string Description, string? VariantName, decimal Quantity, decimal UnitPrice, decimal NetAmount);

public sealed record ReceiptPaymentDto(string Method, decimal Amount);

public sealed record ReceiptDto(
    string StoreName,
    string BranchName,
    string RegisterName,
    string SaleNumber,
    string CashierName,
    DateTime CreatedAtUtc,
    IReadOnlyList<ReceiptLineDto> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    IReadOnlyList<ReceiptPaymentDto> Payments,
    decimal ChangeDue,
    SaleStatus Status);

public interface IReceiptService
{
    Task<ReceiptDto> GetReceiptAsync(Guid saleId, CancellationToken cancellationToken = default);
}

// ---- Returns ----

public sealed record ReturnLineInput(Guid SaleItemId, decimal Quantity, bool Restock = true);

public sealed record CreateReturnRequest(
    IReadOnlyList<ReturnLineInput> Items,
    string Reason,
    PaymentMethod RefundMethod,
    string? RefundReference);

public sealed record SaleReturnItemDto(
    Guid Id,
    Guid SaleItemId,
    Guid ProductVariantId,
    string ProductName,
    decimal Quantity,
    decimal RefundAmount,
    bool Restocked);

public sealed record SaleReturnDto(
    Guid Id,
    string ReturnNumber,
    Guid SaleId,
    string OriginalSaleNumber,
    string Reason,
    decimal TotalRefund,
    Guid CreatedByUserId,
    string CreatedByName,
    DateTime CreatedAtUtc,
    IReadOnlyList<SaleReturnItemDto> Items,
    IReadOnlyList<ReceiptPaymentDto> Refunds);

public interface IReturnService
{
    Task<SaleReturnDto> CreateReturnAsync(Guid saleId, CreateReturnRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SaleReturnDto>> ListReturnsAsync(Guid saleId, CancellationToken cancellationToken = default);
}
