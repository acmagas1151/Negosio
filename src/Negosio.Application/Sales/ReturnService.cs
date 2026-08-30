using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
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

    public ReturnService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateReturnRequest> validator,
        IDocumentNumberService documentNumbers,
        IInventoryPosting inventory,
        SaleQueryService saleQuery)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _documentNumbers = documentNumbers;
        _inventory = inventory;
        _saleQuery = saleQuery;
    }

    public async Task<SaleReturnDto> CreateReturnAsync(Guid saleId, CreateReturnRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var sale = await _db.Sales
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

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

        var returnNumber = await _documentNumbers.NextAsync(tenantId, sale.BranchId, DocumentNumberType.Return, branch.Code, cancellationToken);
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
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var returns = await _saleQuery.LoadReturnsAsync(tenantId, sale.Id, cancellationToken);
        return returns.First(r => r.Id == saleReturn.Id);
    }

    public async Task<IReadOnlyList<SaleReturnDto>> ListReturnsAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var exists = await _db.Sales.AnyAsync(s => s.TenantId == tenantId && s.Id == saleId, cancellationToken);
        if (!exists)
        {
            throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }

        return await _saleQuery.LoadReturnsAsync(tenantId, saleId, cancellationToken);
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
