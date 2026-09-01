using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Branches;

public sealed class BranchQueryService : IBranchQueryService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _resolver;

    public BranchQueryService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver resolver)
    {
        _db = db;
        _currentUser = currentUser;
        _resolver = resolver;
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

        var query = _db.Branches.AsNoTracking().Where(b => b.TenantId == tenantId);

        // Branch-scoped users only ever see their own branch (which, while authenticated, is active).
        var assigned = await _resolver.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } branchId)
        {
            query = query.Where(b => b.Id == branchId);
        }
        else if (!includeInactive)
        {
            query = query.Where(b => b.IsActive);
        }

        return await query
            .OrderBy(b => b.Name)
            .Select(Projection)
            .ToListAsync(cancellationToken);
    }

    private Expression<Func<Branch, BranchDto>> Projection =>
        b => new BranchDto(
            b.Id, b.Name, b.Code, b.AddressLine1, b.AddressLine2, b.City, b.Province, b.PostalCode,
            b.IsActive, b.CreatedAtUtc,
            _db.Users.Count(u => u.BranchId == b.Id && u.IsActive));
}
