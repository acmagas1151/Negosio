using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// A tenant-scoped grouping for products. <see cref="NormalizedName"/> (whitespace-collapsed,
/// upper-cased) is what the database enforces as unique per tenant, so "Beverages", "beverages"
/// and " BEVERAGES " cannot coexist. <see cref="Name"/> keeps the last casing the user entered.
/// </summary>
public class Category : Entity
{
    private Category()
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
    }

    private Category(Guid tenantId, string name, string normalizedName, string? description)
    {
        TenantId = tenantId;
        Name = name;
        NormalizedName = normalizedName;
        Description = description;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; }

    public string NormalizedName { get; private set; }

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    public static Category Create(Guid tenantId, string name, string? description)
    {
        var trimmed = RequireName(name);
        return new Category(tenantId, trimmed, Normalize(trimmed), CleanDescription(description));
    }

    public void UpdateDetails(string name, string? description)
    {
        var trimmed = RequireName(name);
        Name = trimmed;
        NormalizedName = Normalize(trimmed);
        Description = CleanDescription(description);
        Touch();
    }

    public void Activate()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Touch();
    }

    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Touch();
    }

    /// <summary>Single source of truth for category-name comparison. Mirrors <c>User.NormalizeEmail</c>.</summary>
    public static string Normalize(string name)
    {
        var collapsed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToUpperInvariant();
    }

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Category name is required.", nameof(name));
        }

        return name.Trim();
    }

    private static string? CleanDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
