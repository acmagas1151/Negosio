using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;

namespace Negosio.Application.Branches;

public sealed class BranchQueryService : IBranchQueryService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;

    public BranchQueryService(ITenantDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<IReadOnlyList<BranchDto>> ListAsync(
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        // TenantId comes from the authenticated principal; the tenant DB is already the physical boundary.
        var tenantId = _currentUser.TenantId;

        var query = _db.Branches
            .AsNoTracking()
            .Where(b => b.TenantId == tenantId);

        if (!includeInactive)
        {
            query = query.Where(b => b.IsActive);
        }

        return await query
            .OrderBy(b => b.Name)
            .Select(b => new BranchDto(b.Id, b.Name, b.Code, b.IsActive))
            .ToListAsync(cancellationToken);
    }
}
