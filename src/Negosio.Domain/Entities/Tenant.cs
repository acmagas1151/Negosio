using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// The top-level isolation boundary. Every tenant-owned entity references a <see cref="Tenant"/>.
/// </summary>
public class Tenant : Entity
{
    private readonly List<Branch> _branches = new();
    private readonly List<User> _users = new();

    private Tenant()
    {
        Name = string.Empty;
    }

    private Tenant(string name, BusinessType businessType)
    {
        Name = name;
        BusinessType = businessType;
        IsActive = true;
        TaxRatePercent = 0m;
        PricesIncludeTax = false;
    }

    public string Name { get; private set; }

    public BusinessType BusinessType { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Sales tax rate applied at checkout, as a percentage (e.g. 12.00). 0 = no tax.</summary>
    public decimal TaxRatePercent { get; private set; }

    /// <summary>
    /// When true, catalog selling prices already include tax (tax-inclusive). When false (default),
    /// tax is added on top at checkout (tax-exclusive). The two modes are never mixed.
    /// </summary>
    public bool PricesIncludeTax { get; private set; }

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

    public IReadOnlyCollection<Branch> Branches => _branches.AsReadOnly();

    public IReadOnlyCollection<User> Users => _users.AsReadOnly();

    public static Tenant Create(string name, BusinessType businessType)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tenant name is required.", nameof(name));
        }

        return new Tenant(name.Trim(), businessType);
    }

    public Branch AddBranch(
        string name,
        string code,
        string addressLine1,
        string? addressLine2,
        string city,
        string province,
        string? postalCode)
    {
        var branch = Branch.Create(Id, name, code, addressLine1, addressLine2, city, province, postalCode);
        _branches.Add(branch);
        Touch();
        return branch;
    }

    public User AddUser(
        string normalizedEmail,
        string passwordHash,
        string firstName,
        string lastName,
        UserRole role)
    {
        var user = User.Create(Id, normalizedEmail, passwordHash, firstName, lastName, role);
        _users.Add(user);
        Touch();
        return user;
    }
}
