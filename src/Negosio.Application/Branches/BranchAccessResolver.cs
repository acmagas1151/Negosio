using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;

namespace Negosio.Application.Branches;

/// <summary>
/// Turns a request's "which branch?" into an allowed branch id (or a 403/404). Owner/Admin are
/// tenant-wide; every other role is bound to their <c>User.BranchId</c>. Memoized per request.
/// </summary>
public interface IBranchAccessResolver
{
    /// <summary>True for Owner/Admin.</summary>
    bool IsAllBranch { get; }

    /// <summary>Owner/Admin: null. Branch-scoped: the assigned branch id (throws if unassigned).</summary>
    Task<Guid?> AssignedBranchIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// List/read filter. Owner/Admin: returns <paramref name="requested"/> (may be null = all
    /// branches). Branch-scoped: always the assigned branch, ignoring <paramref name="requested"/>.
    /// </summary>
    Task<Guid?> ResolveListFilterAsync(Guid? requested, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write/target branch. Owner/Admin: <paramref name="requested"/> (required), validated to exist.
    /// Branch-scoped: the assigned branch; <c>BRANCH_FORBIDDEN</c> if <paramref name="requested"/>
    /// differs. Asserts the resolved branch is active unless <paramref name="allowInactive"/>.
    /// </summary>
    Task<Guid> ResolveTargetBranchAsync(Guid? requested, bool allowInactive = false, CancellationToken cancellationToken = default);
}

public sealed class BranchAccessResolver : IBranchAccessResolver
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private Guid? _assigned;
    private bool _loaded;

    public BranchAccessResolver(ITenantDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public bool IsAllBranch => BranchRoles.IsAllBranch(_currentUser.Role);

    public async Task<Guid?> AssignedBranchIdAsync(CancellationToken cancellationToken = default)
    {
        if (IsAllBranch)
        {
            return null;
        }

        if (!_loaded)
        {
            _assigned = await _db.Users.AsNoTracking()
                .Where(u => u.Id == _currentUser.UserId)
                .Select(u => u.BranchId)
                .FirstOrDefaultAsync(cancellationToken);
            _loaded = true;
        }

        if (_assigned is null)
        {
            throw new ForbiddenAppException(
                ErrorCodes.BranchForbidden, "Your account is not assigned to a branch. Ask an administrator.");
        }

        return _assigned;
    }

    public async Task<Guid?> ResolveListFilterAsync(Guid? requested, CancellationToken cancellationToken = default)
        => IsAllBranch ? requested : await AssignedBranchIdAsync(cancellationToken);

    public async Task<Guid> ResolveTargetBranchAsync(
        Guid? requested, bool allowInactive = false, CancellationToken cancellationToken = default)
    {
        Guid target;
        if (IsAllBranch)
        {
            target = requested
                ?? throw new BusinessRuleException(ErrorCodes.BranchNotFound, "A branch is required.");
        }
        else
        {
            var assigned = (await AssignedBranchIdAsync(cancellationToken))!.Value;
            if (requested is { } r && r != assigned)
            {
                throw new ForbiddenAppException(
                    ErrorCodes.BranchForbidden, "You can only work in your assigned branch.");
            }

            target = assigned;
        }

        var branch = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == _currentUser.TenantId && b.Id == target)
            .Select(b => new { b.IsActive })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");

        if (!allowInactive && !branch.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.BranchInactive, "This branch is inactive.");
        }

        return target;
    }
}
