using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// A catalog-level product definition. Pricing, SKU and barcode live on <see cref="ProductVariant"/>
/// (the sellable unit), not here. Every product owns at least one variant:
///  - <see cref="HasVariants"/> == false: exactly one hidden <c>IsDefault</c> variant.
///  - <see cref="HasVariants"/> == true: one or more user-defined variants.
/// The simple -> variant transition is supported; the reverse is out of scope for Phase 2.
/// </summary>
public class Product : Entity
{
    private readonly List<ProductVariant> _variants = new();

    private Product()
    {
        Name = string.Empty;
    }

    private Product(Guid tenantId, Guid categoryId, string name, string? description, bool trackInventory)
    {
        TenantId = tenantId;
        CategoryId = categoryId;
        Name = RequireName(name);
        Description = CleanDescription(description);
        TrackInventory = trackInventory;
        IsActive = true;
        HasVariants = false;
    }

    public Guid TenantId { get; private set; }

    public Guid CategoryId { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool TrackInventory { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>True once the product has user-defined variants (the default has been promoted away).</summary>
    public bool HasVariants { get; private set; }

    public IReadOnlyCollection<ProductVariant> Variants => _variants.AsReadOnly();

    /// <summary>Create a product with a single hidden default variant carrying its price/codes.</summary>
    public static Product Create(
        Guid tenantId,
        Guid categoryId,
        string name,
        string? description,
        bool trackInventory,
        VariantSpec defaultVariant)
    {
        var product = new Product(tenantId, categoryId, name, description, trackInventory);
        product._variants.Add(ProductVariant.Create(tenantId, product.Id, isDefault: true, defaultVariant));
        return product;
    }

    public void UpdateDetails(Guid categoryId, string name, string? description, bool trackInventory)
    {
        CategoryId = categoryId;
        Name = RequireName(name);
        Description = CleanDescription(description);
        TrackInventory = trackInventory;
        Touch();
    }

    /// <summary>Edit the hidden default variant of a simple product (from the product form).</summary>
    public void UpdateDefaultVariant(string? sku, string? barcode, decimal costPrice, decimal sellingPrice)
    {
        if (HasVariants)
        {
            throw new InvalidOperationException("This product has variants; edit the variants instead.");
        }

        DefaultVariant.UpdateDefault(sku, barcode, costPrice, sellingPrice);
        Touch();
    }

    /// <summary>
    /// Add a user-defined variant. The first call promotes the hidden default row into that variant;
    /// later calls append new rows.
    /// </summary>
    public ProductVariant AddVariant(VariantSpec spec)
    {
        ProductVariant variant;
        if (!HasVariants)
        {
            variant = DefaultVariant;
            variant.PromoteFromDefault(spec);
            HasVariants = true;
        }
        else
        {
            variant = ProductVariant.Create(TenantId, Id, isDefault: false, spec);
            _variants.Add(variant);
        }

        Touch();
        return variant;
    }

    public void UpdateVariant(Guid variantId, VariantSpec spec)
    {
        RequireRealVariant(variantId).Update(spec);
        Touch();
    }

    public void DeactivateVariant(Guid variantId)
    {
        var variant = RequireRealVariant(variantId);
        if (variant.IsActive && _variants.Count(v => v.IsActive) == 1)
        {
            throw new InvalidOperationException("A product must keep at least one active variant.");
        }

        variant.Deactivate();
        Touch();
    }

    public void ActivateVariant(Guid variantId)
    {
        RequireRealVariant(variantId).Activate();
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

    public ProductVariant DefaultVariant =>
        _variants.SingleOrDefault(v => v.IsDefault)
        ?? throw new InvalidOperationException("This product has no default variant.");

    /// <summary>The variant a bare-product inventory adjustment resolves to (simple products only).</summary>
    public ProductVariant? ResolveSellableVariant(Guid? variantId)
    {
        if (variantId is { } id)
        {
            return _variants.SingleOrDefault(v => v.Id == id);
        }

        return HasVariants ? null : DefaultVariant;
    }

    private ProductVariant RequireRealVariant(Guid variantId) =>
        _variants.SingleOrDefault(v => v.Id == variantId && !v.IsDefault)
        ?? throw new KeyNotFoundException($"Variant {variantId} was not found on this product.");

    private static string RequireName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Product name is required.", nameof(name));
        }

        return name.Trim();
    }

    private static string? CleanDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description.Trim();
}
