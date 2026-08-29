using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>One returned line: which original <see cref="SaleItem"/>, how much came back, refund and restock.</summary>
public class SaleReturnItem : Entity
{
    private SaleReturnItem()
    {
        ProductNameSnapshot = string.Empty;
    }

    internal SaleReturnItem(
        Guid tenantId,
        Guid saleReturnId,
        Guid saleItemId,
        Guid productVariantId,
        string productNameSnapshot,
        decimal quantity,
        decimal refundAmount,
        bool restocked)
    {
        TenantId = tenantId;
        SaleReturnId = saleReturnId;
        SaleItemId = saleItemId;
        ProductVariantId = productVariantId;
        ProductNameSnapshot = productNameSnapshot;
        Quantity = quantity;
        RefundAmount = refundAmount;
        Restocked = restocked;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleReturnId { get; private set; }

    public Guid SaleItemId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal RefundAmount { get; private set; }

    public bool Restocked { get; private set; }
}
