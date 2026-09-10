using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>One line item in a <see cref="DeliveryReceipt"/>: a snapshot of product details at delivery time.</summary>
public class DeliveryReceiptItem : Entity
{
    private DeliveryReceiptItem()
    {
        ProductNameSnapshot = string.Empty;
    }

    internal DeliveryReceiptItem(
        Guid tenantId,
        Guid deliveryReceiptId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        decimal quantity,
        decimal? unitPrice)
    {
        TenantId = tenantId;
        DeliveryReceiptId = deliveryReceiptId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid TenantId { get; private set; }

    public Guid DeliveryReceiptId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? VariantNameSnapshot { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal? UnitPrice { get; private set; }
}
