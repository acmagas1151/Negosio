using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Inventory;

public enum InventoryStatus
{
    OutOfStock = 0,
    LowStock = 1,
    InStock = 2
}

public sealed record InventoryRowDto(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid ProductId,
    string ProductName,
    Guid ProductVariantId,
    string VariantName,
    bool IsDefaultVariant,
    string? Sku,
    decimal QuantityOnHand,
    decimal ReorderLevel,
    InventoryStatus Status,
    decimal? CostPrice,
    decimal SellingPrice,
    string ConcurrencyToken,
    DateTime UpdatedAtUtc);

public sealed record StockMovementDto(
    Guid Id,
    Guid BranchId,
    string BranchName,
    Guid ProductId,
    string ProductName,
    Guid ProductVariantId,
    string VariantName,
    StockMovementType Type,
    decimal Quantity,
    decimal QuantityBefore,
    decimal QuantityAfter,
    string? Reason,
    Guid CreatedByUserId,
    string CreatedByName,
    DateTime CreatedAtUtc);

public sealed record AdjustInventoryRequest(
    Guid BranchId,
    Guid ProductId,
    Guid? ProductVariantId,
    decimal Adjustment,
    string Reason,
    decimal? ReorderLevel,
    string? ExpectedConcurrencyToken);

public sealed record InventoryListQuery(
    Guid? BranchId = null,
    Guid? ProductId = null,
    Guid? CategoryId = null,
    string? Search = null,
    bool? LowStock = null,
    int Page = 1,
    int PageSize = PagedResult<InventoryRowDto>.DefaultPageSize);

public sealed record MovementListQuery(
    Guid? BranchId = null,
    Guid? ProductId = null,
    Guid? ProductVariantId = null,
    StockMovementType? Type = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Page = 1,
    int PageSize = PagedResult<StockMovementDto>.DefaultPageSize);

public interface IInventoryService
{
    Task<PagedResult<InventoryRowDto>> ListAsync(InventoryListQuery query, CancellationToken cancellationToken = default);

    Task<InventoryRowDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<InventoryRowDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken cancellationToken = default);

    Task<PagedResult<StockMovementDto>> ListMovementsAsync(MovementListQuery query, CancellationToken cancellationToken = default);
}
