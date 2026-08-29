using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// The quantity of one sellable item (<see cref="ProductVariant"/>) held at one branch. Identity is
/// (Tenant, Branch, ProductVariant) — never a bare product. Only the inventory application service
/// mutates this, always alongside a <see cref="StockMovement"/>. <see cref="RowVersion"/> is a
/// SQL Server <c>rowversion</c> concurrency token.
/// </summary>
public class BranchInventory : Entity
{
    private BranchInventory()
    {
    }

    private BranchInventory(Guid tenantId, Guid branchId, Guid productVariantId, decimal reorderLevel)
    {
        TenantId = tenantId;
        BranchId = branchId;
        ProductVariantId = productVariantId;
        QuantityOnHand = 0m;
        ReorderLevel = RequireNonNegative(reorderLevel, nameof(reorderLevel));
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid ProductVariantId { get; private set; }

    public decimal QuantityOnHand { get; private set; }

    public decimal ReorderLevel { get; private set; }

    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public bool IsLow => QuantityOnHand <= ReorderLevel;

    public static BranchInventory Create(Guid tenantId, Guid branchId, Guid productVariantId, decimal reorderLevel) =>
        new(tenantId, branchId, productVariantId, reorderLevel);

    /// <summary>Apply a signed delta. Returns the quantity before/after. Never allows a negative result.</summary>
    public (decimal Before, decimal After) ApplyAdjustment(decimal delta)
    {
        var before = QuantityOnHand;
        var after = before + delta;
        if (after < 0)
        {
            throw new InvalidOperationException("Inventory quantity cannot fall below zero.");
        }

        QuantityOnHand = after;
        Touch();
        return (before, after);
    }

    public void SetReorderLevel(decimal reorderLevel)
    {
        ReorderLevel = RequireNonNegative(reorderLevel, nameof(reorderLevel));
        Touch();
    }

    private static decimal RequireNonNegative(decimal value, string name) =>
        value < 0 ? throw new ArgumentOutOfRangeException(name, "Value cannot be negative.") : value;
}
