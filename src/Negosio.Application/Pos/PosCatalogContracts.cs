using Negosio.Application.Common;

namespace Negosio.Application.Pos;

/// <summary>
/// Exactly what a cashier screen needs — never cost price, margin or internal inventory audit fields.
/// </summary>
public sealed record PosCatalogItemDto(
    Guid ProductId,
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    string? Sku,
    string? Barcode,
    decimal SellingPrice,
    decimal QuantityAvailable,
    bool TrackInventory,
    bool IsAvailable);

public sealed record PosCatalogQuery(
    Guid BranchId,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<PosCatalogItemDto>.DefaultPageSize);

public interface IPosCatalogService
{
    Task<PagedResult<PosCatalogItemDto>> SearchAsync(PosCatalogQuery query, CancellationToken cancellationToken = default);

    Task<PosCatalogItemDto> BarcodeLookupAsync(Guid branchId, string barcode, CancellationToken cancellationToken = default);
}
