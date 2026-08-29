using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Dashboard;

public sealed record DashboardTenantDto(Guid Id, string Name, BusinessType BusinessType);

public sealed record DashboardResponse(DashboardTenantDto Tenant, int BranchCount, int UserCount);

public interface IDashboardService
{
    Task<DashboardResponse> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class DashboardService : IDashboardService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;

    public DashboardService(IApplicationDbContext db, ICurrentUser currentUser)
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

        var tenant = await _db.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant not found.");

        var branchCount = await _db.Branches.CountAsync(b => b.TenantId == tenantId, cancellationToken);
        var userCount = await _db.Users.CountAsync(u => u.TenantId == tenantId, cancellationToken);

        return new DashboardResponse(
            new DashboardTenantDto(tenant.Id, tenant.Name, tenant.BusinessType),
            branchCount,
            userCount);
    }
}
