using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

public sealed class SalesVoidPermissionService : ISalesVoidPermissionService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;

    public SalesVoidPermissionService(ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
    }

    public async Task<bool> HasGrantAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        return await _db.UserPermissionGrants.AsNoTracking()
            .AnyAsync(g => g.TenantId == tenantId && g.UserId == userId && g.Permission == UserPermission.SalesVoid, cancellationToken);
    }

    public async Task<StaffMemberDto> SetAsync(Guid targetUserId, bool salesVoid, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        if (targetUserId == _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.SalesVoidSelfGrant, "You cannot change your own void permission.");
        }

        var target = await _db.Users.SingleOrDefaultAsync(u => u.TenantId == tenantId && u.Id == targetUserId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

        if (target.Role != UserRole.Cashier)
        {
            throw new BusinessRuleException(ErrorCodes.SalesVoidGrantRoleInvalid, "Only a Cashier can hold this permission.");
        }

        if (!BranchRoles.IsAllBranch(_currentUser.Role))
        {
            var assigned = (await _branchAccess.AssignedBranchIdAsync(cancellationToken))!.Value;
            if (target.BranchId != assigned)
            {
                throw new ForbiddenAppException(ErrorCodes.BranchForbidden, "You can only manage this permission for staff in your own branch.");
            }
        }

        var existing = await _db.UserPermissionGrants
            .SingleOrDefaultAsync(g => g.TenantId == tenantId && g.UserId == targetUserId && g.Permission == UserPermission.SalesVoid, cancellationToken);

        if (salesVoid && existing is null)
        {
            _db.UserPermissionGrants.Add(UserPermissionGrant.Grant(tenantId, targetUserId, UserPermission.SalesVoid, _currentUser.UserId));
        }
        else if (!salesVoid && existing is not null)
        {
            _db.UserPermissionGrants.Remove(existing);
        }

        await _db.SaveChangesAsync(cancellationToken);

        string? branchName = target.BranchId is { } bid
            ? await _db.Branches.AsNoTracking().Where(b => b.Id == bid).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new StaffMemberDto(
            target.Id, StaffMemberKind.Member, target.FirstName, target.LastName, target.Email, target.Role,
            target.IsActive ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
            JoinedAtUtc: target.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null,
            BranchId: target.BranchId, BranchName: branchName, SalesVoid: salesVoid);
    }
}
