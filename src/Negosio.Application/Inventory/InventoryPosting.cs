using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Inventory;

/// <summary>
/// Programmatic inventory postings for sales and returns. Unlike the management adjustment path
/// (<see cref="InventoryService.AdjustAsync"/>, which takes a UI rowversion token), these run inside
/// the caller's checkout/return transaction and prevent overselling with an **atomic conditional
/// UPDATE** (`... WHERE QuantityOnHand >= @qty`). Each posting also writes one <see cref="StockMovement"/>.
/// </summary>
public interface IInventoryPosting
{
    /// <summary>
    /// Decrement stock for a sale line. Throws <see cref="ConflictException"/>
    /// (<see cref="ErrorCodes.InsufficientInventory"/>) if the row is missing or has less than
    /// <paramref name="quantity"/> on hand — the caller must roll back.
    /// </summary>
    Task DeductForSaleAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
        Guid saleId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Increment stock for a returned line (never fails).</summary>
    Task RestockForReturnAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
        Guid saleReturnId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Restore stock for a voided sale line (never fails, mirrors RestockForReturnAsync).</summary>
    Task ReverseForVoidAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
        Guid saleId, Guid userId, CancellationToken cancellationToken = default);
}

public sealed class InventoryPosting : IInventoryPosting
{
    private readonly ITenantDbContext _db;

    public InventoryPosting(ITenantDbContext db)
    {
        _db = db;
    }

    public async Task DeductForSaleAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
        Guid saleId, Guid userId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        var affected = await _db.BranchInventories
            .Where(i => i.TenantId == tenantId
                        && i.BranchId == branchId
                        && i.ProductVariantId == productVariantId
                        && i.QuantityOnHand >= quantity)
            .ExecuteUpdateAsync(
                s => s.SetProperty(i => i.QuantityOnHand, i => i.QuantityOnHand - quantity)
                      .SetProperty(i => i.UpdatedAtUtc, _ => now),
                cancellationToken);

        if (affected == 0)
        {
            throw new ConflictException(ErrorCodes.InsufficientInventory,
                "Not enough stock to complete this sale.");
        }

        var after = await _db.BranchInventories
            .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == productVariantId)
            .Select(i => i.QuantityOnHand)
            .SingleAsync(cancellationToken);

        _db.StockMovements.Add(StockMovement.Create(
            tenantId, branchId, productVariantId, StockMovementType.Sale,
            -quantity, after + quantity, after, reason: null, userId,
            referenceType: "Sale", referenceId: saleId));
    }

    public Task RestockForReturnAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
        Guid saleReturnId, Guid userId, CancellationToken cancellationToken = default) =>
        RestockAsync(tenantId, branchId, productVariantId, quantity, userId,
            StockMovementType.Return, referenceType: "Return", referenceId: saleReturnId, cancellationToken);

    public Task ReverseForVoidAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity,
        Guid saleId, Guid userId, CancellationToken cancellationToken = default) =>
        RestockAsync(tenantId, branchId, productVariantId, quantity, userId,
            StockMovementType.SaleVoid, referenceType: "Sale", referenceId: saleId, cancellationToken);

    private async Task RestockAsync(
        Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity, Guid userId,
        StockMovementType type, string referenceType, Guid referenceId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var affected = await _db.BranchInventories
            .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == productVariantId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(i => i.QuantityOnHand, i => i.QuantityOnHand + quantity)
                      .SetProperty(i => i.UpdatedAtUtc, _ => now),
                cancellationToken);

        decimal after;
        decimal before;
        if (affected == 0)
        {
            // No inventory row yet (opening stock was never recorded) — create it at the returned qty.
            var created = BranchInventory.Create(tenantId, branchId, productVariantId, 0m);
            created.ApplyAdjustment(quantity);
            _db.BranchInventories.Add(created);
            before = 0m;
            after = quantity;
        }
        else
        {
            after = await _db.BranchInventories
                .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == productVariantId)
                .Select(i => i.QuantityOnHand)
                .SingleAsync(cancellationToken);
            before = after - quantity;
        }

        _db.StockMovements.Add(StockMovement.Create(
            tenantId, branchId, productVariantId, type, quantity, before, after, reason: null, userId,
            referenceType, referenceId));
    }
}
