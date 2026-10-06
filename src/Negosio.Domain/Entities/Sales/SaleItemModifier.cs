using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// One modifier chosen for a <see cref="SaleItem"/>, frozen from the RestoPOS order item at settlement.
/// Names and price delta are snapshots: a later menu change never alters a settled receipt. A sale line
/// with no modifiers simply has no rows here, so Retail sales are unaffected.
/// </summary>
public class SaleItemModifier : Entity
{
    private SaleItemModifier()
    {
        ModifierGroupNameSnapshot = string.Empty;
        ModifierOptionNameSnapshot = string.Empty;
    }

    internal SaleItemModifier(
        Guid tenantId, Guid saleItemId, string modifierGroupNameSnapshot, string modifierOptionNameSnapshot,
        decimal priceDeltaSnapshot, int sortOrder)
    {
        if (string.IsNullOrWhiteSpace(modifierGroupNameSnapshot))
        {
            throw new ArgumentException("Modifier group name is required.", nameof(modifierGroupNameSnapshot));
        }

        if (string.IsNullOrWhiteSpace(modifierOptionNameSnapshot))
        {
            throw new ArgumentException("Modifier option name is required.", nameof(modifierOptionNameSnapshot));
        }

        if (priceDeltaSnapshot < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceDeltaSnapshot), "A modifier's price delta cannot be negative.");
        }

        TenantId = tenantId;
        SaleItemId = saleItemId;
        ModifierGroupNameSnapshot = modifierGroupNameSnapshot.Trim();
        ModifierOptionNameSnapshot = modifierOptionNameSnapshot.Trim();
        PriceDeltaSnapshot = priceDeltaSnapshot;
        SortOrder = sortOrder;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleItemId { get; private set; }

    public string ModifierGroupNameSnapshot { get; private set; }

    public string ModifierOptionNameSnapshot { get; private set; }

    public decimal PriceDeltaSnapshot { get; private set; }

    public int SortOrder { get; private set; }
}
