using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// The sellable unit. Every <see cref="Product"/> has at least one. A product with no
/// user-defined variants carries a single <see cref="IsDefault"/> variant that the API hides and
/// the product form edits inline. SKU / barcode / prices live here (not on <see cref="Product"/>)
/// so SQL Server can enforce per-tenant uniqueness with one filtered index per column.
/// </summary>
public class ProductVariant : Entity
{
    private ProductVariant()
    {
    }

    private ProductVariant(Guid tenantId, Guid productId, bool isDefault, VariantSpec spec)
    {
        TenantId = tenantId;
        ProductId = productId;
        IsDefault = isDefault;
        IsActive = true;
        Apply(spec);
    }

    public Guid TenantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>True for the auto-created stand-in of a product that has no user-defined variants.</summary>
    public bool IsDefault { get; private set; }

    public string? Sku { get; private set; }

    public string? Barcode { get; private set; }

    public decimal CostPrice { get; private set; }

    public decimal SellingPrice { get; private set; }

    public bool IsActive { get; private set; }

    internal static ProductVariant Create(Guid tenantId, Guid productId, bool isDefault, VariantSpec spec) =>
        new(tenantId, productId, isDefault, spec);

    /// <summary>Promote the hidden default variant into the product's first real variant.</summary>
    internal void PromoteFromDefault(VariantSpec spec)
    {
        IsDefault = false;
        Apply(spec);
    }

    internal void Update(VariantSpec spec)
    {
        Apply(spec);
        Touch();
    }

    /// <summary>Update only the pricing/identity of a simple product's default variant.</summary>
    internal void UpdateDefault(string? sku, string? barcode, decimal costPrice, decimal sellingPrice)
    {
        Apply(new VariantSpec(Name, sku, barcode, costPrice, sellingPrice));
        Touch();
    }

    internal void Activate()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Touch();
    }

    internal void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Touch();
    }

    private void Apply(VariantSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Name))
        {
            throw new ArgumentException("Variant name is required.", nameof(spec));
        }

        if (spec.CostPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "Cost price cannot be negative.");
        }

        if (spec.SellingPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "Selling price cannot be negative.");
        }

        Name = spec.Name.Trim();
        Sku = Clean(spec.Sku);
        Barcode = Clean(spec.Barcode);
        CostPrice = spec.CostPrice;
        SellingPrice = spec.SellingPrice;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
