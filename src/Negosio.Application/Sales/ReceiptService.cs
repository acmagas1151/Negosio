using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;

namespace Negosio.Application.Sales;

public sealed class ReceiptService : IReceiptService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ReceiptService(ITenantDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<ReceiptDto> GetReceiptAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        var storeName = await _db.TenantProfiles.Where(p => p.Id == tenantId).Select(p => p.Name).SingleAsync(cancellationToken);
        var branchName = await _db.Branches.Where(b => b.Id == sale.BranchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var registerName = await _db.RegisterSessions.Where(rs => rs.Id == sale.RegisterSessionId)
            .Join(_db.Registers, rs => rs.RegisterId, r => r.Id, (_, r) => r.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        var cashierName = await _db.Users.Where(u => u.Id == sale.CreatedByUserId)
            .Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var lines = sale.Items
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i => new ReceiptLineDto(i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, i.UnitPrice, i.NetAmount))
            .ToList();

        var payments = sale.Payments
            .OrderBy(p => p.CreatedAtUtc)
            .Select(p => new ReceiptPaymentDto(p.Method.ToString(), p.Amount))
            .ToList();

        return new ReceiptDto(
            storeName, branchName, registerName, sale.SaleNumber, cashierName,
            sale.CompletedAtUtc ?? sale.CreatedAtUtc, lines,
            sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.GrandTotal,
            payments, sale.ChangeDue, sale.Status);
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
