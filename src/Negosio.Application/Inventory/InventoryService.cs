using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Catalog;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Inventory;

public sealed class InventoryService : IInventoryService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<AdjustInventoryRequest> _adjustValidator;
    private readonly IBranchAccessResolver _branchAccess;

    public InventoryService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<AdjustInventoryRequest> adjustValidator,
        IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _adjustValidator = adjustValidator;
        _branchAccess = branchAccess;
    }

    public async Task<PagedResult<InventoryRowDto>> ListAsync(InventoryListQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var canViewCost = CatalogAccess.CanViewCost(_currentUser.Role);
        query = query with { BranchId = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken) };

        var rows =
            from i in _db.BranchInventories.AsNoTracking().Where(i => i.TenantId == tenantId)
            join v in _db.ProductVariants on i.ProductVariantId equals v.Id
            join p in _db.Products on v.ProductId equals p.Id
            join b in _db.Branches on i.BranchId equals b.Id
            select new { i, v, p, b };

        if (query.BranchId is { } branchId)
        {
            rows = rows.Where(x => x.i.BranchId == branchId);
        }

        if (query.ProductId is { } productId)
        {
            rows = rows.Where(x => x.p.Id == productId);
        }

        if (query.CategoryId is { } categoryId)
        {
            rows = rows.Where(x => x.p.CategoryId == categoryId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            rows = rows.Where(x =>
                x.p.Name.Contains(term) ||
                (x.v.Sku != null && x.v.Sku.Contains(term)) ||
                (x.v.Barcode != null && x.v.Barcode.Contains(term)));
        }

        if (query.LowStock == true)
        {
            rows = rows.Where(x => x.i.QuantityOnHand <= x.i.ReorderLevel);
        }

        var projected = rows
            .OrderBy(x => x.p.Name).ThenBy(x => x.v.Name)
            .Select(x => new InventoryRaw(
                x.i.Id, x.i.BranchId, x.b.Name, x.p.Id, x.p.Name, x.v.Id, x.v.Name, x.v.IsDefault,
                x.v.Sku, x.i.QuantityOnHand, x.i.ReorderLevel, x.v.CostPrice, x.v.SellingPrice,
                x.i.RowVersion, x.i.UpdatedAtUtc));

        var raw = await PagedResult<InventoryRaw>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = raw.Items.Select(r => ToDto(r, canViewCost)).ToList();

        return new PagedResult<InventoryRowDto>(items, raw.Page, raw.PageSize, raw.TotalCount, raw.TotalPages);
    }

    public async Task<InventoryRowDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var canViewCost = CatalogAccess.CanViewCost(_currentUser.Role);

        var raw = await (
            from i in _db.BranchInventories.AsNoTracking().Where(i => i.TenantId == tenantId && i.Id == id)
            join v in _db.ProductVariants on i.ProductVariantId equals v.Id
            join p in _db.Products on v.ProductId equals p.Id
            join b in _db.Branches on i.BranchId equals b.Id
            select new InventoryRaw(
                i.Id, i.BranchId, b.Name, p.Id, p.Name, v.Id, v.Name, v.IsDefault,
                v.Sku, i.QuantityOnHand, i.ReorderLevel, v.CostPrice, v.SellingPrice,
                i.RowVersion, i.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return raw is null
            ? throw new NotFoundException(ErrorCodes.InventoryNotFound, "Inventory record not found.")
            : ToDto(raw, canViewCost);
    }

    public async Task<InventoryRowDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _adjustValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var branchId = await _branchAccess.ResolveTargetBranchAsync(request.BranchId, cancellationToken: cancellationToken);
        var branch = await _db.Branches
            .SingleAsync(b => b.TenantId == tenantId && b.Id == branchId, cancellationToken);

        var product = await _db.Products
            .Include(p => p.Variants)
            .SingleOrDefaultAsync(p => p.TenantId == tenantId && p.Id == request.ProductId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.ProductNotFound, "Product not found.");

        if (!product.TrackInventory)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidInventoryAdjustment, "This product is not inventory-tracked.");
        }

        var variant = product.ResolveSellableVariant(request.ProductVariantId);
        if (variant is null)
        {
            throw request.ProductVariantId is null
                ? new BusinessRuleException(ErrorCodes.VariantRequired, "This product has variants; specify which variant to adjust.")
                : new NotFoundException(ErrorCodes.VariantNotFound, "Variant not found.");
        }

        if (!variant.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidInventoryAdjustment, "This variant is inactive.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var inventory = await _db.BranchInventories.SingleOrDefaultAsync(
            i => i.TenantId == tenantId && i.BranchId == branch.Id && i.ProductVariantId == variant.Id,
            cancellationToken);

        StockMovementType type;
        if (inventory is null)
        {
            inventory = BranchInventory.Create(tenantId, branch.Id, variant.Id, request.ReorderLevel ?? 0m);
            _db.BranchInventories.Add(inventory);
            type = StockMovementType.OpeningStock;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.ExpectedConcurrencyToken))
            {
                throw new BusinessRuleException(
                    ErrorCodes.InvalidInventoryAdjustment,
                    "A concurrency token is required when adjusting existing inventory.");
            }

            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(request.ExpectedConcurrencyToken);
            }
            catch (FormatException)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidInventoryAdjustment, "The concurrency token is malformed.");
            }

            _db.Entry(inventory).Property(i => i.RowVersion).OriginalValue = expected;

            if (request.ReorderLevel is { } reorderLevel)
            {
                inventory.SetReorderLevel(reorderLevel);
            }

            type = request.Adjustment >= 0 ? StockMovementType.AdjustmentIncrease : StockMovementType.AdjustmentDecrease;
        }

        if (inventory.QuantityOnHand + request.Adjustment < 0)
        {
            throw new BusinessRuleException(ErrorCodes.InsufficientInventory, "Inventory cannot fall below zero.");
        }

        var (before, after) = inventory.ApplyAdjustment(request.Adjustment);

        _db.StockMovements.Add(StockMovement.Create(
            tenantId, branch.Id, variant.Id, type, request.Adjustment, before, after, request.Reason, _currentUser.UserId));

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ConflictException(
                ErrorCodes.InventoryConcurrencyConflict,
                "Inventory was modified by another operation. Please retry.");
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ConflictException(
                ErrorCodes.InventoryConcurrencyConflict,
                "Inventory was modified by another operation. Please retry.");
        }

        return await GetAsync(inventory.Id, cancellationToken);
    }

    public async Task<PagedResult<StockMovementDto>> ListMovementsAsync(MovementListQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        query = query with { BranchId = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken) };

        var rows =
            from m in _db.StockMovements.AsNoTracking().Where(m => m.TenantId == tenantId)
            join v in _db.ProductVariants on m.ProductVariantId equals v.Id
            join p in _db.Products on v.ProductId equals p.Id
            join b in _db.Branches on m.BranchId equals b.Id
            select new { m, v, p, b };

        if (query.BranchId is { } branchId)
        {
            rows = rows.Where(x => x.m.BranchId == branchId);
        }

        if (query.ProductId is { } productId)
        {
            rows = rows.Where(x => x.p.Id == productId);
        }

        if (query.ProductVariantId is { } variantId)
        {
            rows = rows.Where(x => x.m.ProductVariantId == variantId);
        }

        if (query.Type is { } type)
        {
            rows = rows.Where(x => x.m.Type == type);
        }

        if (query.FromUtc is { } fromUtc)
        {
            rows = rows.Where(x => x.m.CreatedAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            rows = rows.Where(x => x.m.CreatedAtUtc <= toUtc);
        }

        var projected = rows
            .OrderByDescending(x => x.m.CreatedAtUtc)
            .Select(x => new StockMovementDto(
                x.m.Id,
                x.m.BranchId,
                x.b.Name,
                x.p.Id,
                x.p.Name,
                x.m.ProductVariantId,
                x.v.Name,
                x.m.Type,
                x.m.Quantity,
                x.m.QuantityBefore,
                x.m.QuantityAfter,
                x.m.Reason,
                x.m.CreatedByUserId,
                _db.Users.Where(u => u.Id == x.m.CreatedByUserId)
                    .Select(u => u.FirstName + " " + u.LastName)
                    .FirstOrDefault() ?? string.Empty,
                x.m.CreatedAtUtc));

        return await PagedResult<StockMovementDto>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
    }

    private sealed record InventoryRaw(
        Guid Id, Guid BranchId, string BranchName, Guid ProductId, string ProductName,
        Guid ProductVariantId, string VariantName, bool IsDefaultVariant, string? Sku,
        decimal QuantityOnHand, decimal ReorderLevel, decimal CostPrice, decimal SellingPrice,
        byte[] RowVersion, DateTime UpdatedAtUtc);

    private static InventoryRowDto ToDto(InventoryRaw r, bool canViewCost) =>
        new(
            r.Id,
            r.BranchId,
            r.BranchName,
            r.ProductId,
            r.ProductName,
            r.ProductVariantId,
            r.VariantName,
            r.IsDefaultVariant,
            r.Sku,
            r.QuantityOnHand,
            r.ReorderLevel,
            r.QuantityOnHand <= 0m
                ? InventoryStatus.OutOfStock
                : r.QuantityOnHand <= r.ReorderLevel
                    ? InventoryStatus.LowStock
                    : InventoryStatus.InStock,
            canViewCost ? r.CostPrice : null,
            r.SellingPrice,
            Convert.ToBase64String(r.RowVersion),
            r.UpdatedAtUtc);

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
