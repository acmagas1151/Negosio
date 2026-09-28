using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>A reusable, tenant-owned set of selectable options (e.g. "Add-ons", "Spice Level")
/// attachable to any number of products via <see cref="ProductModifierGroup"/>. Tenant-wide, not
/// branch-scoped — a menu's modifier definitions are shared across every branch that sells the
/// product, unlike <see cref="RestoStation"/>.</summary>
public class ModifierGroup : Entity
{
    private readonly List<ModifierOption> _options = new();

    private ModifierGroup()
    {
        Name = string.Empty;
    }

    private ModifierGroup(Guid tenantId, string name, ModifierSelectionType selectionType)
    {
        TenantId = tenantId;
        Name = name;
        SelectionType = selectionType;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public ModifierSelectionType SelectionType { get; private set; }

    public bool IsActive { get; private set; }

    public IReadOnlyCollection<ModifierOption> Options => _options.AsReadOnly();

    public static ModifierGroup Create(Guid tenantId, string name, ModifierSelectionType selectionType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Modifier group name is required.", nameof(name));
        }

        return new ModifierGroup(tenantId, name.Trim(), selectionType);
    }

    public ModifierOption AddOption(string name, decimal priceDelta)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Option name is required.", nameof(name));
        }

        if (priceDelta < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(priceDelta), "A modifier's price delta cannot be negative.");
        }

        var option = new ModifierOption(TenantId, Id, name.Trim(), priceDelta);
        _options.Add(option);
        return option;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Modifier group name is required.", nameof(name));
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
