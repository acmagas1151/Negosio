using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>Join row: which <see cref="ModifierGroup"/>s a given <c>Product</c> offers, and whether a
/// selection from that group is required for that specific product (e.g. "Spice Level" might be
/// required on a curry but not offered at all on a drink).</summary>
public class ProductModifierGroup : Entity
{
    private ProductModifierGroup()
    {
    }

    private ProductModifierGroup(Guid tenantId, Guid productId, Guid modifierGroupId, bool isRequired, int displayOrder)
    {
        TenantId = tenantId;
        ProductId = productId;
        ModifierGroupId = modifierGroupId;
        IsRequired = isRequired;
        DisplayOrder = displayOrder;
    }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public Guid ModifierGroupId { get; private set; }

    public bool IsRequired { get; private set; }

    public int DisplayOrder { get; private set; }

    public static ProductModifierGroup Create(Guid tenantId, Guid productId, Guid modifierGroupId, bool isRequired, int displayOrder) =>
        new(tenantId, productId, modifierGroupId, isRequired, displayOrder);
}
