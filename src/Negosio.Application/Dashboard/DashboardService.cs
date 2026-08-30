using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Dashboard;

public sealed record DashboardTenantDto(Guid Id, string Name, BusinessType BusinessType);

public sealed record DashboardResponse(
    DashboardTenantDto Tenant,
    int BranchCount,
    int UserCount,
    int TotalProducts,
    int ActiveCategories,
    int LowStockItems,
    decimal TodaysSales,
    int TodaysTransactions,
    decimal AverageTransactionValue);

public interface IDashboardService
{
    Task<DashboardResponse> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class DashboardService : IDashboardService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;

    public DashboardService(ITenantDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<DashboardResponse> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        // TenantId always comes from the authenticated principal, never from the request.
        var tenantId = _currentUser.TenantId;

        var profile = await _db.TenantProfiles
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant not found.");

        var branchCount = await _db.Branches.CountAsync(b => b.TenantId == tenantId, cancellationToken);
        var userCount = await _db.Users.CountAsync(u => u.TenantId == tenantId, cancellationToken);
        var totalProducts = await _db.Products.CountAsync(p => p.TenantId == tenantId && p.IsActive, cancellationToken);
        var activeCategories = await _db.Categories.CountAsync(c => c.TenantId == tenantId && c.IsActive, cancellationToken);
        var lowStockItems = await _db.BranchInventories
            .CountAsync(i => i.TenantId == tenantId && i.QuantityOnHand <= i.ReorderLevel, cancellationToken);

        // Today's sales: SQL aggregates only, no row materialisation.
        var startOfDayUtc = DateTime.UtcNow.Date;
        var todaysSalesQuery = _db.Sales.Where(s =>
            s.TenantId == tenantId
            && (s.Status == SaleStatus.Completed || s.Status == SaleStatus.PartiallyRefunded)
            && s.CompletedAtUtc >= startOfDayUtc);

        var todaysSales = await todaysSalesQuery.SumAsync(s => (decimal?)s.GrandTotal, cancellationToken) ?? 0m;
        var todaysTransactions = await todaysSalesQuery.CountAsync(cancellationToken);
        var averageTransactionValue = todaysTransactions == 0
            ? 0m
            : Math.Round(todaysSales / todaysTransactions, 2, MidpointRounding.AwayFromZero);

        return new DashboardResponse(
            new DashboardTenantDto(profile.Id, profile.Name, profile.BusinessType),
            branchCount,
            userCount,
            totalProducts,
            activeCategories,
            lowStockItems,
            todaysSales,
            todaysTransactions,
            averageTransactionValue);
    }
}
