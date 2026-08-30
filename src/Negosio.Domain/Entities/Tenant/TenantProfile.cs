using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// The single operational profile row inside a tenant database. Mirrors the platform
/// <see cref="Tenant"/>'s identity (<see cref="Name"/>, <see cref="BusinessType"/>) so receipts,
/// dashboards and <c>/auth/me</c> never need a platform lookup, and owns the operational tax
/// configuration used at checkout. <see cref="Id"/> equals the tenant id.
/// </summary>
public class TenantProfile : Entity
{
    private TenantProfile()
    {
        Name = string.Empty;
    }

    private TenantProfile(Guid tenantId, string name, BusinessType businessType)
    {
        Id = tenantId;
        Name = name;
        BusinessType = businessType;
        TaxRatePercent = 0m;
        PricesIncludeTax = false;
    }

    public string Name { get; private set; }

    public BusinessType BusinessType { get; private set; }

    /// <summary>Sales tax rate applied at checkout, as a percentage (e.g. 12.00). 0 = no tax.</summary>
    public decimal TaxRatePercent { get; private set; }

    /// <summary>
    /// When true, catalog selling prices already include tax (tax-inclusive). When false (default),
    /// tax is added on top at checkout (tax-exclusive). The two modes are never mixed.
    /// </summary>
    public bool PricesIncludeTax { get; private set; }

    public static TenantProfile Create(Guid tenantId, string name, BusinessType businessType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name is required.", nameof(name));
        }

        return new TenantProfile(tenantId, name.Trim(), businessType);
    }

    public void ConfigureTax(decimal taxRatePercent, bool pricesIncludeTax)
    {
        if (taxRatePercent < 0m || taxRatePercent > 100m)
        {
            throw new ArgumentOutOfRangeException(nameof(taxRatePercent), "Tax rate must be between 0 and 100.");
        }

        TaxRatePercent = taxRatePercent;
        PricesIncludeTax = pricesIncludeTax;
        Touch();
    }
}
