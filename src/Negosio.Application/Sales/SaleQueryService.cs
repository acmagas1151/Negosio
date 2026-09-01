using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Catalog;
using Negosio.Application.Common;

namespace Negosio.Application.Sales;

public sealed class SaleQueryService : ISaleQueryService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;

    public SaleQueryService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
    }

    public async Task<PagedResult<SaleSummaryDto>> ListAsync(SaleListQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        query = query with { BranchId = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken) };

        var sales = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId);

        if (query.BranchId is { } branchId)
        {
            sales = sales.Where(s => s.BranchId == branchId);
        }

        if (query.RegisterId is { } registerId)
        {
            sales = sales.Where(s => _db.RegisterSessions.Any(rs => rs.Id == s.RegisterSessionId && rs.RegisterId == registerId));
        }

        if (query.CashierUserId is { } cashier)
        {
            sales = sales.Where(s => s.CreatedByUserId == cashier);
        }

        if (query.Status is { } status)
        {
            sales = sales.Where(s => s.Status == status);
        }

        if (query.FromUtc is { } fromUtc)
        {
            sales = sales.Where(s => s.CreatedAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            sales = sales.Where(s => s.CreatedAtUtc <= toUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            sales = sales.Where(s => s.SaleNumber.Contains(term));
        }

        var projected = sales
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new SaleSummaryRow(
                s.Id,
                s.SaleNumber,
                s.BranchId,
                _db.Branches.Where(b => b.Id == s.BranchId).Select(b => b.Name).FirstOrDefault() ?? string.Empty,
                s.CreatedByUserId,
                _db.Users.Where(u => u.Id == s.CreatedByUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault() ?? string.Empty,
                _db.SaleItems.Count(i => i.SaleId == s.Id),
                s.GrandTotal,
                s.Status,
                _db.Payments.Where(p => p.SaleId == s.Id).Select(p => p.Method).Distinct().ToList(),
                s.CreatedAtUtc));

        var rows = await PagedResult<SaleSummaryRow>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = rows.Items.Select(r => new SaleSummaryDto(
            r.Id, r.SaleNumber, r.BranchId, r.BranchName, r.CashierUserId, r.CashierName, r.ItemCount,
            r.GrandTotal, r.Status, string.Join(" + ", r.Methods.Select(m => m.ToString())), r.CreatedAtUtc)).ToList();

        return new PagedResult<SaleSummaryDto>(items, rows.Page, rows.PageSize, rows.TotalCount, rows.TotalPages);
    }

    private sealed record SaleSummaryRow(
        Guid Id, string SaleNumber, Guid BranchId, string BranchName, Guid CashierUserId, string CashierName,
        int ItemCount, decimal GrandTotal, Domain.Enums.SaleStatus Status,
        List<Domain.Enums.PaymentMethod> Methods, DateTime CreatedAtUtc);

    public async Task<SaleDetailDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var canViewCost = CatalogAccess.CanViewCost(_currentUser.Role);

        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        await GuardBranchAsync(sale.BranchId, cancellationToken);

        var branchName = await _db.Branches.Where(b => b.Id == sale.BranchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var cashierName = await _db.Users.Where(u => u.Id == sale.CreatedByUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var summary = new SaleSummaryDto(
            sale.Id, sale.SaleNumber, sale.BranchId, branchName, sale.CreatedByUserId, cashierName,
            sale.Items.Count, sale.GrandTotal, sale.Status,
            string.Join(" + ", sale.Payments.Select(p => p.Method.ToString()).Distinct()), sale.CreatedAtUtc);

        var items = sale.Items
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i => new SaleItemDto(
                i.Id, i.ProductVariantId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.SkuSnapshot, i.BarcodeSnapshot,
                i.UnitPrice, i.Quantity, i.GrossAmount, i.DiscountAmount, i.TaxAmount, i.NetAmount,
                canViewCost ? i.CostPriceSnapshot : null, i.ReturnedQuantity))
            .ToList();

        var payments = sale.Payments
            .OrderBy(p => p.CreatedAtUtc)
            .Select(p => new SalePaymentDto(p.Id, p.Method, p.Amount, p.ReferenceNumber, p.ReceivedAmount, p.ChangeAmount))
            .ToList();

        var returns = await LoadReturnsAsync(tenantId, sale.Id, cancellationToken);

        return new SaleDetailDto(
            summary, sale.RegisterSessionId, sale.Subtotal, sale.DiscountTotal, sale.TaxTotal,
            sale.AmountPaid, sale.ChangeDue, sale.CompletedAtUtc, items, payments, returns);
    }

    internal async Task<IReadOnlyList<SaleReturnDto>> LoadReturnsAsync(Guid tenantId, Guid saleId, CancellationToken cancellationToken)
    {
        var returns = await _db.SaleReturns.AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Refunds)
            .Where(r => r.TenantId == tenantId && r.SaleId == saleId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        // Every return here belongs to the one `saleId`, so the originating sale number is a single
        // lookup, not one per return.
        var originalSaleNumber = await _db.Sales.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Id == saleId)
            .Select(s => s.SaleNumber)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var userNames = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, cancellationToken);

        return returns.Select(r => new SaleReturnDto(
            r.Id, r.ReturnNumber, r.SaleId, originalSaleNumber, r.Reason, r.TotalRefund, r.CreatedByUserId,
            userNames.GetValueOrDefault(r.CreatedByUserId, string.Empty), r.CreatedAtUtc,
            r.Items.Select(i => new SaleReturnItemDto(
                i.Id, i.SaleItemId, i.ProductVariantId, i.ProductNameSnapshot, i.Quantity, i.RefundAmount, i.Restocked)).ToList(),
            r.Refunds.Select(rp => new ReceiptPaymentDto(rp.Method.ToString(), rp.Amount)).ToList()))
            .ToList();
    }

    /// <summary>A branch-scoped user may not see another branch's sale — 404, not 403, to hide its existence.</summary>
    internal async Task GuardBranchAsync(Guid saleBranchId, CancellationToken cancellationToken)
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
