using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

public sealed class ReturnService : IReturnService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateReturnRequest> _validator;
    private readonly IDocumentNumberService _documentNumbers;
    private readonly IInventoryPosting _inventory;
    private readonly SaleQueryService _saleQuery;
    private readonly IBranchAccessResolver _branchAccess;

    public ReturnService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateReturnRequest> validator,
        IDocumentNumberService documentNumbers,
        IInventoryPosting inventory,
        SaleQueryService saleQuery,
        IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _documentNumbers = documentNumbers;
        _inventory = inventory;
        _saleQuery = saleQuery;
        _branchAccess = branchAccess;
    }

    public async Task<SaleReturnDto> CreateReturnAsync(Guid saleId, CreateReturnRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var sale = await _db.Sales
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        await GuardSaleBranchAsync(sale.BranchId, cancellationToken);

        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded))
        {
            throw new BusinessRuleException(ErrorCodes.ReturnNotAllowed, "This sale cannot be returned against.");
        }

        var branch = await _db.Branches.SingleAsync(b => b.Id == sale.BranchId, cancellationToken);
        var tenant = await _db.TenantProfiles.SingleAsync(p => p.Id == tenantId, cancellationToken);

        // Which returned variants belong to inventory-tracked products?
        var lineVariantIds = sale.Items.Select(i => i.ProductVariantId).ToList();
        var trackedVariantIds = (await _db.ProductVariants.AsNoTracking()
            .Where(v => v.TenantId == tenantId && lineVariantIds.Contains(v.Id))
            .Join(_db.Products.Where(p => p.TrackInventory), v => v.ProductId, p => p.Id, (v, _) => v.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Returns draw from the same branch transaction sequence as sales (DocumentNumberType.Sale),
        // so a return gets the next running number after the last sale or return in the branch.
        var returnNumber = await _documentNumbers.NextAsync(tenantId, sale.BranchId, DocumentNumberType.Sale, branch.Code, cancellationToken);
        var saleReturn = SaleReturn.Begin(tenantId, sale.Id, sale.BranchId, returnNumber, _currentUser.UserId, request.Reason);

        foreach (var lineInput in request.Items)
        {
            var saleItem = sale.Items.SingleOrDefault(i => i.Id == lineInput.SaleItemId)
                ?? throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "A return line refers to an item that is not on this sale.");

            if (lineInput.Quantity <= 0m)
            {
                throw new BusinessRuleException(ErrorCodes.InvalidQuantity, "Return quantity must be greater than zero.");
            }

            if (lineInput.Quantity > saleItem.ReturnableQuantity)
            {
                throw new BusinessRuleException(ErrorCodes.ReturnQuantityExceeded, "Cannot return more than was purchased.");
            }

            var paidPerLine = saleItem.NetAmount + (tenant.PricesIncludeTax ? 0m : saleItem.TaxAmount);
            var refund = Money.Round(paidPerLine * lineInput.Quantity / saleItem.Quantity);

            var willRestock = lineInput.Restock && trackedVariantIds.Contains(saleItem.ProductVariantId);

            saleItem.RecordReturn(lineInput.Quantity);
            saleReturn.AddItem(saleItem.Id, saleItem.ProductVariantId, saleItem.ProductNameSnapshot, lineInput.Quantity, refund, willRestock);

            if (willRestock)
            {
                await _inventory.RestockForReturnAsync(
                    tenantId, sale.BranchId, saleItem.ProductVariantId, lineInput.Quantity, saleReturn.Id, _currentUser.UserId, cancellationToken);
            }
        }

        saleReturn.AddRefund(request.RefundMethod, saleReturn.TotalRefund, request.RefundReference);
        sale.MarkReturned();

        _db.SaleReturns.Add(saleReturn);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Sale.RowVersion caught a race against a concurrent void that committed first — the
            // `await using` transaction above rolls back on this exception path before we get here
            // (we never called CommitAsync), so there is no explicit RollbackAsync needed.
            var fresh = await _db.Sales.AsNoTracking()
                .SingleAsync(s => s.TenantId == tenantId && s.Id == sale.Id, cancellationToken);
            throw new BusinessRuleException(ErrorCodes.ReturnNotAllowed,
                fresh.Status == SaleStatus.Voided
                    ? "This sale was voided and cannot be returned against."
                    : "This sale cannot be returned against.");
        }

        await transaction.CommitAsync(cancellationToken);

        var returns = await _saleQuery.LoadReturnsAsync(tenantId, sale.Id, cancellationToken);
        return returns.First(r => r.Id == saleReturn.Id);
    }

    public async Task<IReadOnlyList<SaleReturnDto>> ListReturnsAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var branchId = await _db.Sales.Where(s => s.TenantId == tenantId && s.Id == saleId)
            .Select(s => (Guid?)s.BranchId).FirstOrDefaultAsync(cancellationToken);
        if (branchId is null)
        {
            throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }

        await GuardSaleBranchAsync(branchId.Value, cancellationToken);

        return await _saleQuery.LoadReturnsAsync(tenantId, saleId, cancellationToken);
    }

    /// <summary>
    /// Branch-scoped users may only act on their own branch's sales (404, not 403). Owner/Admin are
    /// unrestricted — and returns against an inactive historical branch must still work, so there is
    /// no active-branch check here.
    /// </summary>
    private async Task GuardSaleBranchAsync(Guid saleBranchId, CancellationToken cancellationToken)
    {
        var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } branchId && branchId != saleBranchId)
        {
            throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }
    }

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
