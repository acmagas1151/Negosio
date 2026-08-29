namespace Negosio.Domain.Entities;

/// <summary>
/// The mutable fields of a sellable item (<see cref="ProductVariant"/>). Passed into the
/// <see cref="Product"/> aggregate when creating a product or adding/updating a variant.
/// </summary>
public sealed record VariantSpec(
    string Name,
    string? Sku,
    string? Barcode,
    decimal CostPrice,
    decimal SellingPrice);
