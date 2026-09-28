using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>A branch-configurable kitchen/bar station (Kitchen, Bar, Dessert, Grill, ...) that a menu
/// item routes to. Deliberately its own entity rather than a hardcoded enum or free string, so a
/// tenant can name their own stations without a code change — mirrors <c>Category</c>'s shape.
/// Branch-scoped, not tenant-wide: a physical station genuinely differs per branch, the same way a
/// <see cref="Register"/> does.
///
/// Disabling/renaming must never rewrite an existing <c>RestoOrderItem</c> — every item snapshots
/// both this station's <see cref="Id"/> and its <see cref="Name"/> at add-time (see
/// <c>RestoOrderItem.StationNameSnapshot</c>). The rule that a station cannot be disabled while it
/// still has non-terminal (unserved) items routed to it is a cross-aggregate check that belongs to a
/// future application service, not this entity — this class only flips <see cref="IsActive"/>.</summary>
public class RestoStation : Entity
{
    private RestoStation()
    {
        Name = string.Empty;
    }

    private RestoStation(Guid tenantId, Guid branchId, string name)
    {
        TenantId = tenantId;
        BranchId = branchId;
        Name = name;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; }

    public bool IsActive { get; private set; }

    public static RestoStation Create(Guid tenantId, Guid branchId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Station name is required.", nameof(name));
        }

        return new RestoStation(tenantId, branchId, name.Trim());
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Station name is required.", nameof(name));
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
