using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

/// <summary>
/// Reads AND writes <see cref="UserPermissionGrant"/> rows for every grantable <see cref="UserPermission"/>
/// — the single generalized replacement for what used to be a SalesVoid-only
/// <c>SalesVoidPermissionService</c>. Adding a new grantable permission (as <see cref="UserPermission.SalesReturn"/>
/// was for this pass) needs no new service: it's just another enum value and another field on
/// <see cref="ChangeStaffPermissionsRequest"/>/<see cref="StaffMemberDto"/>.
/// </summary>
public interface IUserPermissionGrantService
{
    Task<bool> HasGrantAsync(Guid userId, UserPermission permission, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants or revokes one or more permissions for <paramref name="targetUserId"/> in a single
    /// atomic write. Every entry shares the same safeguards (self-grant forbidden, target must be a
    /// Cashier, branch-scoped managers may only act on their own branch) — these don't vary per
    /// permission, so they're checked once for the whole batch rather than once per entry.
    /// </summary>
    Task<StaffMemberDto> SetAsync(
        Guid targetUserId, IReadOnlyDictionary<UserPermission, bool> grants, CancellationToken cancellationToken = default);
}

public sealed class UserPermissionGrantService : IUserPermissionGrantService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;

    public UserPermissionGrantService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
    }

    public async Task<bool> HasGrantAsync(Guid userId, UserPermission permission, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        return await _db.UserPermissionGrants.AsNoTracking()
            .AnyAsync(g => g.TenantId == tenantId && g.UserId == userId && g.Permission == permission, cancellationToken);
    }

    public async Task<StaffMemberDto> SetAsync(
        Guid targetUserId, IReadOnlyDictionary<UserPermission, bool> grants, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        if (targetUserId == _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.PermissionSelfGrant, "You cannot change your own permissions.");
        }

        var target = await _db.Users.SingleOrDefaultAsync(u => u.TenantId == tenantId && u.Id == targetUserId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

        if (target.Role != UserRole.Cashier)
        {
            throw new BusinessRuleException(ErrorCodes.PermissionGrantRoleInvalid, "Only a Cashier can hold these permissions.");
        }

        if (!BranchRoles.IsAllBranch(_currentUser.Role))
        {
            var assigned = (await _branchAccess.AssignedBranchIdAsync(cancellationToken))!.Value;
            if (target.BranchId != assigned)
            {
                throw new ForbiddenAppException(ErrorCodes.BranchForbidden, "You can only manage permissions for staff in your own branch.");
            }
        }

        var existing = await _db.UserPermissionGrants
            .Where(g => g.TenantId == tenantId && g.UserId == targetUserId && grants.Keys.Contains(g.Permission))
            .ToListAsync(cancellationToken);

        foreach (var (permission, granted) in grants)
        {
            var row = existing.SingleOrDefault(g => g.Permission == permission);
            if (granted && row is null)
            {
                _db.UserPermissionGrants.Add(UserPermissionGrant.Grant(tenantId, targetUserId, permission, _currentUser.UserId));
            }
            else if (!granted && row is not null)
            {
                _db.UserPermissionGrants.Remove(row);
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        string? branchName = target.BranchId is { } bid
            ? await _db.Branches.AsNoTracking().Where(b => b.Id == bid).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new StaffMemberDto(
            target.Id, StaffMemberKind.Member, target.FirstName, target.LastName, target.Email, target.Role,
            target.IsActive ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
            JoinedAtUtc: target.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null,
            BranchId: target.BranchId, BranchName: branchName,
            SalesVoid: grants.GetValueOrDefault(UserPermission.SalesVoid),
            SalesReturn: grants.GetValueOrDefault(UserPermission.SalesReturn),
            DiscountApply: grants.GetValueOrDefault(UserPermission.DiscountApply),
            CashDrawerOpen: grants.GetValueOrDefault(UserPermission.CashDrawerOpen));
    }
}
