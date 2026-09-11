using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>One line item in a <see cref="DeliveryReceipt"/>: which <see cref="SaleItem"/> and how
/// much of it is assigned to this specific delivery, plus a snapshot of product details at delivery
/// time (never re-read from the live Product record). <see cref="SaleItemId"/> is what fulfillment
/// allocation (Pending/Delivered/Unscheduled quantity per sale item) is computed from.</summary>
public class DeliveryReceiptItem : Entity
{
    private DeliveryReceiptItem()
    {
        ProductNameSnapshot = string.Empty;
    }

    internal DeliveryReceiptItem(
        Guid tenantId,
        Guid deliveryReceiptId,
        Guid saleItemId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        decimal quantity,
        decimal? unitPrice)
    {
        TenantId = tenantId;
        DeliveryReceiptId = deliveryReceiptId;
        SaleItemId = saleItemId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid TenantId { get; private set; }

    public Guid DeliveryReceiptId { get; private set; }

    public Guid SaleItemId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? VariantNameSnapshot { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal? UnitPrice { get; private set; }
}
