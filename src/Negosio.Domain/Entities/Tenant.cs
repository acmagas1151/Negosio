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
    }

    public string Name { get; private set; }

    public BusinessType BusinessType { get; private set; }

    public bool IsActive { get; private set; }

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
