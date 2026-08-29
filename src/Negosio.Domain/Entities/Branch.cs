using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// A physical location belonging to a tenant. <see cref="Code"/> is unique within a tenant.
/// </summary>
public class Branch : Entity
{
    private Branch()
    {
        Name = string.Empty;
        Code = string.Empty;
        AddressLine1 = string.Empty;
        City = string.Empty;
        Province = string.Empty;
    }

    private Branch(
        Guid tenantId,
        string name,
        string code,
        string addressLine1,
        string? addressLine2,
        string city,
        string province,
        string? postalCode)
    {
        TenantId = tenantId;
        Name = name;
        Code = code;
        AddressLine1 = addressLine1;
        AddressLine2 = addressLine2;
        City = city;
        Province = province;
        PostalCode = postalCode;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string Code { get; private set; }

    public string AddressLine1 { get; private set; }

    public string? AddressLine2 { get; private set; }

    public string City { get; private set; }

    public string Province { get; private set; }

    public string? PostalCode { get; private set; }

    public bool IsActive { get; private set; }

    public static Branch Create(
        Guid tenantId,
        string name,
        string code,
        string addressLine1,
        string? addressLine2,
        string city,
        string province,
        string? postalCode)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Branch name is required.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Branch code is required.", nameof(code));
        }

        return new Branch(
            tenantId,
            name.Trim(),
            code.Trim().ToUpperInvariant(),
            addressLine1?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(addressLine2) ? null : addressLine2.Trim(),
            city?.Trim() ?? string.Empty,
            province?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim());
    }
}
