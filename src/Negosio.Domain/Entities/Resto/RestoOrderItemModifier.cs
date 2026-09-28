using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>A frozen record of one modifier chosen for a <see cref="RestoOrderItem"/> — both the
/// group and option names, and the price delta, are snapshotted at add-time so a later catalog edit
/// to the live <see cref="ModifierGroup"/>/<see cref="ModifierOption"/> can never change an open bill
/// or a historical order (same discipline as every other snapshot field in this module).</summary>
public class RestoOrderItemModifier : Entity
{
    private RestoOrderItemModifier()
    {
        ModifierGroupNameSnapshot = string.Empty;
        ModifierOptionNameSnapshot = string.Empty;
    }

    private RestoOrderItemModifier(
        Guid tenantId, Guid restoOrderItemId, string modifierGroupNameSnapshot, string modifierOptionNameSnapshot, decimal priceDeltaSnapshot)
    {
        TenantId = tenantId;
        RestoOrderItemId = restoOrderItemId;
        ModifierGroupNameSnapshot = modifierGroupNameSnapshot;
        ModifierOptionNameSnapshot = modifierOptionNameSnapshot;
        PriceDeltaSnapshot = priceDeltaSnapshot;
    }

    public Guid TenantId { get; private set; }

    public Guid RestoOrderItemId { get; private set; }

    public string ModifierGroupNameSnapshot { get; private set; }

    public string ModifierOptionNameSnapshot { get; private set; }

    public decimal PriceDeltaSnapshot { get; private set; }

    internal static RestoOrderItemModifier Create(
        Guid tenantId, Guid restoOrderItemId, string modifierGroupNameSnapshot, string modifierOptionNameSnapshot, decimal priceDeltaSnapshot) =>
        new(tenantId, restoOrderItemId, modifierGroupNameSnapshot, modifierOptionNameSnapshot, priceDeltaSnapshot);
}
