using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A completed retail sale. Built by the checkout use case inside one database transaction together
/// with its <see cref="Items"/>, <see cref="Payments"/>, inventory deductions and stock movements.
/// Totals are computed backend-side; the client's figures are never trusted.
/// </summary>
public class Sale : Entity
{
    private readonly List<SaleItem> _items = new();
    private readonly List<Payment> _payments = new();

    private Sale()
    {
        SaleNumber = string.Empty;
    }

    private Sale(
        Guid tenantId,
        Guid branchId,
        Guid registerSessionId,
        string saleNumber,
        Guid clientRequestId,
        Guid createdByUserId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterSessionId = registerSessionId;
        SaleNumber = saleNumber;
        ClientRequestId = clientRequestId;
        CreatedByUserId = createdByUserId;
        Status = SaleStatus.Completed;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterSessionId { get; private set; }

    public string SaleNumber { get; private set; }

    public Guid ClientRequestId { get; private set; }

    public SaleStatus Status { get; private set; }

    public decimal Subtotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    public decimal GrandTotal { get; private set; }

    public decimal AmountPaid { get; private set; }

    public decimal ChangeDue { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public DateTime? CompletedAtUtc { get; private set; }

    public DateTime? VoidedAtUtc { get; private set; }

    public string? VoidReason { get; private set; }

    public IReadOnlyCollection<SaleItem> Items => _items.AsReadOnly();

    public IReadOnlyCollection<Payment> Payments => _payments.AsReadOnly();

    public static Sale Begin(
        Guid tenantId,
        Guid branchId,
        Guid registerSessionId,
        string saleNumber,
        Guid clientRequestId,
        Guid createdByUserId) =>
        new(tenantId, branchId, registerSessionId, saleNumber, clientRequestId, createdByUserId);

    public SaleItem AddItem(
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        string? skuSnapshot,
        string? barcodeSnapshot,
        decimal unitPrice,
        decimal quantity,
        DiscountType discountKind,
        decimal discountValue,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal? costPriceSnapshot)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        var item = new SaleItem(
            TenantId, Id, productVariantId, productNameSnapshot, variantNameSnapshot, skuSnapshot,
            barcodeSnapshot, unitPrice, quantity, discountKind, discountValue, grossAmount,
            discountAmount, taxAmount, netAmount, costPriceSnapshot);
        _items.Add(item);
        return item;
    }

    public Payment AddPayment(
        PaymentMethod method,
        decimal amount,
        string? referenceNumber,
        decimal? receivedAmount,
        decimal? changeAmount)
    {
        var payment = new Payment(TenantId, Id, method, amount, referenceNumber, receivedAmount, changeAmount);
        _payments.Add(payment);
        return payment;
    }

    public void Complete(decimal subtotal, decimal discountTotal, decimal taxTotal, decimal grandTotal, decimal amountPaid, decimal changeDue)
    {
        Subtotal = subtotal;
        DiscountTotal = discountTotal;
        TaxTotal = taxTotal;
        GrandTotal = grandTotal;
        AmountPaid = amountPaid;
        ChangeDue = changeDue;
        Status = SaleStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }

    /// <summary>Called by the return use case after recording a return against this sale.</summary>
    public void MarkReturned()
    {
        Status = _items.All(i => i.ReturnableQuantity <= 0m)
            ? SaleStatus.Refunded
            : SaleStatus.PartiallyRefunded;
        Touch();
    }
}
