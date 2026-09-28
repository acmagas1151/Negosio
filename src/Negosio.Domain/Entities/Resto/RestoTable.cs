using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>A physical (or logical) dine-in table a Bill-Out <c>RestoOrder</c> is opened against.
/// Branch-scoped. Deliberately no position/layout fields — a floor-plan UI is out of scope for this
/// phase; this exists purely so a <c>RestoOrder</c> can hold a real foreign key instead of a free-text
/// label, which is what lets the filtered unique index on <c>RestoOrders(TableId) WHERE Status=Open</c>
/// (see <c>RestoOrderConfiguration</c>) actually prevent two concurrent visits on the same table.</summary>
public class RestoTable : Entity
{
    private RestoTable()
    {
        Name = string.Empty;
    }

    private RestoTable(Guid tenantId, Guid branchId, string name)
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

    public static RestoTable Create(Guid tenantId, Guid branchId, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name is required.", nameof(name));
        }

        return new RestoTable(tenantId, branchId, name.Trim());
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Table name is required.", nameof(name));
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
