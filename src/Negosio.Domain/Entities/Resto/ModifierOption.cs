using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>One selectable choice within a <see cref="ModifierGroup"/> (e.g. "Extra cheese", "+20").
/// A menu-time definition — see <c>RestoOrderItemModifier</c> for the frozen snapshot taken when a
/// customer actually picks one.</summary>
public class ModifierOption : Entity
{
    private ModifierOption()
    {
        Name = string.Empty;
    }

    internal ModifierOption(Guid tenantId, Guid modifierGroupId, string name, decimal priceDelta)
    {
        TenantId = tenantId;
        ModifierGroupId = modifierGroupId;
        Name = name;
        PriceDelta = priceDelta;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Guid ModifierGroupId { get; private set; }

    public string Name { get; private set; }

    public decimal PriceDelta { get; private set; }

    public bool IsActive { get; private set; }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Option name is required.", nameof(name));
        }

        Name = name.Trim();
        Touch();
    }

    public void Disable()
    {
        IsActive = false;
        Touch();
    }

    public void Enable()
    {
        IsActive = true;
        Touch();
    }
}
