using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// One line of a <see cref="Sale"/>. Carries transaction-time **snapshots** of the product name,
/// variant name, SKU, barcode, unit price and cost so a later rename or reprice never changes an
/// old receipt. All monetary amounts are computed backend-side at checkout.
/// </summary>
public class SaleItem : Entity
{
    private SaleItem()
    {
        ProductNameSnapshot = string.Empty;
    }

    internal SaleItem(
        Guid tenantId,
        Guid saleId,
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
        TenantId = tenantId;
        SaleId = saleId;
        ProductVariantId = productVariantId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        SkuSnapshot = skuSnapshot;
        BarcodeSnapshot = barcodeSnapshot;
        UnitPrice = unitPrice;
        Quantity = quantity;
        DiscountKind = discountKind;
        DiscountValue = discountValue;
        GrossAmount = grossAmount;
        DiscountAmount = discountAmount;
        TaxAmount = taxAmount;
        NetAmount = netAmount;
        CostPriceSnapshot = costPriceSnapshot;
        ReturnedQuantity = 0m;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? VariantNameSnapshot { get; private set; }

    public string? SkuSnapshot { get; private set; }

    public string? BarcodeSnapshot { get; private set; }

    public decimal UnitPrice { get; private set; }

    public decimal Quantity { get; private set; }

    public DiscountType DiscountKind { get; private set; }

    public decimal DiscountValue { get; private set; }

    public decimal GrossAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal NetAmount { get; private set; }

    public decimal? CostPriceSnapshot { get; private set; }

    /// <summary>Running total of quantity returned against this line. Guarded by the return service.</summary>
    public decimal ReturnedQuantity { get; private set; }

    public decimal ReturnableQuantity => Quantity - ReturnedQuantity;

    public void RecordReturn(decimal quantity)
    {
        if (quantity <= 0m || quantity > ReturnableQuantity)
        {
            throw new InvalidOperationException("Return quantity exceeds what remains on this line.");
        }

        ReturnedQuantity += quantity;
        Touch();
    }
}
