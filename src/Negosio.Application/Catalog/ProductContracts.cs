using Negosio.Application.Common;

namespace Negosio.Application.Catalog;

public sealed record ProductVariantDto(
    Guid Id,
    string Name,
    bool IsDefault,
    string? Sku,
    string? Barcode,
    decimal? CostPrice,
    decimal SellingPrice,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ProductDto(
    Guid Id,
    Guid CategoryId,
    string CategoryName,
    string Name,
    string? Description,
    bool TrackInventory,
    bool IsActive,
    bool HasVariants,
    int VariantCount,
    string? Sku,
    string? Barcode,
    decimal? MinCostPrice,
    decimal? MaxCostPrice,
    decimal MinSellingPrice,
    decimal MaxSellingPrice,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record ProductDetailDto(ProductDto Product, IReadOnlyList<ProductVariantDto> Variants);

public sealed record VariantInput(string Name, string? Sku, string? Barcode, decimal CostPrice, decimal SellingPrice);

public sealed record CreateProductRequest(
    Guid CategoryId,
    string Name,
    string? Description,
    bool TrackInventory,
    string? Sku,
    string? Barcode,
    decimal CostPrice,
    decimal SellingPrice,
    IReadOnlyList<VariantInput>? Variants);

public sealed record UpdateProductRequest(
    Guid CategoryId,
    string Name,
    string? Description,
    bool TrackInventory,
    bool IsActive,
    string? Sku,
    string? Barcode,
    decimal CostPrice,
    decimal SellingPrice);

public sealed record ProductListQuery(
    string? Search = null,
    Guid? CategoryId = null,
    bool? IsActive = null,
    bool? TrackInventory = null,
    int Page = 1,
    int PageSize = PagedResult<ProductDto>.DefaultPageSize,
    string? SortBy = null,
    string? SortDirection = null);

public interface IProductService
{
    Task<PagedResult<ProductDto>> ListAsync(ProductListQuery query, CancellationToken cancellationToken = default);

    Task<ProductDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ProductDetailDto> CreateAsync(CreateProductRequest request, CancellationToken cancellationToken = default);

    Task<ProductDetailDto> UpdateAsync(Guid id, UpdateProductRequest request, CancellationToken cancellationToken = default);

    Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
