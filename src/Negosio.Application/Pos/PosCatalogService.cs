using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;

namespace Negosio.Application.Pos;

public sealed class PosCatalogService : IPosCatalogService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;

    public PosCatalogService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
    }

    private sealed record Row(
        Guid ProductId, Guid ProductVariantId, string ProductName, bool IsDefaultVariant, string VariantName,
        string? Sku, string? Barcode, decimal SellingPrice, decimal QuantityAvailable, bool TrackInventory);

    public async Task<PagedResult<PosCatalogItemDto>> SearchAsync(PosCatalogQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var branchId = await _branchAccess.ResolveTargetBranchAsync(query.BranchId, cancellationToken: cancellationToken);

        var q = from v in _db.ProductVariants.AsNoTracking().Where(v => v.TenantId == tenantId && v.IsActive)
                join p in _db.Products.Where(p => p.IsActive) on v.ProductId equals p.Id
                select new { p, v };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(x =>
                x.p.Name.Contains(term) ||
                (x.v.Sku != null && x.v.Sku.Contains(term)) ||
                (x.v.Barcode != null && x.v.Barcode.Contains(term)));
        }

        if (query.CategoryId is { } categoryId)
        {
            q = q.Where(x => x.p.CategoryId == categoryId);
        }

        var projected = q
            .OrderBy(x => x.p.Name).ThenBy(x => x.v.Name)
            .Select(x => new Row(
                x.p.Id, x.v.Id, x.p.Name, x.v.IsDefault, x.v.Name, x.v.Sku, x.v.Barcode, x.v.SellingPrice,
                _db.BranchInventories
                    .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == x.v.Id)
                    .Select(i => (decimal?)i.QuantityOnHand).FirstOrDefault() ?? 0m,
                x.p.TrackInventory));

        var page = await PagedResult<Row>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = page.Items.Select(ToDto).ToList();

        return new PagedResult<PosCatalogItemDto>(items, page.Page, page.PageSize, page.TotalCount, page.TotalPages);
    }

    public async Task<PosCatalogItemDto> BarcodeLookupAsync(Guid branchId, string barcode, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        branchId = await _branchAccess.ResolveTargetBranchAsync(branchId, cancellationToken: cancellationToken);

        var trimmed = (barcode ?? string.Empty).Trim();

        var row = await (
            from v in _db.ProductVariants.AsNoTracking().Where(v => v.TenantId == tenantId && v.IsActive && v.Barcode == trimmed)
            join p in _db.Products.Where(p => p.IsActive) on v.ProductId equals p.Id
            select new Row(
                p.Id, v.Id, p.Name, v.IsDefault, v.Name, v.Sku, v.Barcode, v.SellingPrice,
                _db.BranchInventories
                    .Where(i => i.TenantId == tenantId && i.BranchId == branchId && i.ProductVariantId == v.Id)
                    .Select(i => (decimal?)i.QuantityOnHand).FirstOrDefault() ?? 0m,
                p.TrackInventory))
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? throw new NotFoundException(ErrorCodes.ProductNotFound, "No product matches this barcode.")
            : ToDto(row);
    }

    private static PosCatalogItemDto ToDto(Row r) => new(
        r.ProductId,
        r.ProductVariantId,
        r.ProductName,
        r.IsDefaultVariant ? null : r.VariantName,
        r.Sku,
        r.Barcode,
        r.SellingPrice,
        r.QuantityAvailable,
        r.TrackInventory,
        !r.TrackInventory || r.QuantityAvailable > 0m);

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
