using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

public sealed class StaffService : IStaffService
{
    private const int InvitationLifetimeDays = 7;

    private readonly IPlatformDbContext _platform;
    private readonly ITenantDbContext _tenant;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<InviteStaffRequest> _inviteValidator;
    private readonly IValidator<ChangeStaffRoleRequest> _roleValidator;
    private readonly IAppEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StaffService> _logger;
    private readonly IBranchAccessResolver _branchAccess;

    private readonly IValidator<ChangeStaffBranchRequest> _branchValidator;

    public StaffService(
        IPlatformDbContext platform,
        ITenantDbContext tenant,
        ICurrentUser currentUser,
        IValidator<InviteStaffRequest> inviteValidator,
        IValidator<ChangeStaffRoleRequest> roleValidator,
        IValidator<ChangeStaffBranchRequest> branchValidator,
        IAppEnvironment environment,
        TimeProvider timeProvider,
        ILogger<StaffService> logger,
        IBranchAccessResolver branchAccess)
    {
        _platform = platform;
        _tenant = tenant;
        _currentUser = currentUser;
        _inviteValidator = inviteValidator;
        _roleValidator = roleValidator;
        _branchValidator = branchValidator;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
        _branchAccess = branchAccess;
    }

    private Guid TenantId => _currentUser.IsAuthenticated
        ? _currentUser.TenantId
        : throw new UnauthorizedAppException("Not authenticated.");

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<StaffMemberDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;

        var users = await _tenant.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId)
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.Role, u.IsActive, u.CreatedAtUtc, u.BranchId })
            .ToListAsync(cancellationToken);

        var branchNames = await _tenant.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        var userIds = users.Select(u => u.Id).ToList();
        var logins = await _platform.PlatformUserLogins.AsNoTracking()
            .Where(l => l.TenantId == tenantId && userIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => new { l.Role, l.IsActive }, cancellationToken);

        var names = users.ToDictionary(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());

        var grantedUserIds = (await _tenant.UserPermissionGrants.AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.Permission == UserPermission.SalesVoid)
            .Select(g => g.UserId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var members = users.Select(u =>
        {
            // Platform is the authority for role + active state; fall back to the tenant row.
            var role = logins.TryGetValue(u.Id, out var l) ? l.Role : u.Role;
            var active = logins.TryGetValue(u.Id, out var l2) ? l2.IsActive : u.IsActive;
            return new StaffMemberDto(
                u.Id, StaffMemberKind.Member, u.FirstName, u.LastName, u.Email, role,
                active ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
                JoinedAtUtc: u.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null,
                BranchId: u.BranchId,
                BranchName: u.BranchId is { } bid ? branchNames.GetValueOrDefault(bid) : null,
                SalesVoid: grantedUserIds.Contains(u.Id) && role == UserRole.Cashier);
        });

        var now = UtcNow;
        var invitations = await _platform.StaffInvitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        var invitationRows = invitations.Select(i => new StaffMemberDto(
            i.Id, StaffMemberKind.Invitation, null, null, i.EmailNormalized, i.Role,
            now >= i.ExpiresAtUtc ? StaffMemberStatus.Expired : StaffMemberStatus.Invited,
            JoinedAtUtc: null, InvitedAtUtc: i.CreatedAtUtc, ExpiresAtUtc: i.ExpiresAtUtc,
            InvitedByName: names.GetValueOrDefault(i.InvitedByUserId),
            BranchId: i.BranchId,
            BranchName: i.BranchId is { } bid ? branchNames.GetValueOrDefault(bid) : null,
            SalesVoid: false));

        var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } branchId)
        {
            // Strictly BranchId == branchId — NOT `|| BranchId == null`. A null BranchId always means
            // Owner/Admin (Phase 5 invariant); a Manager must never see those rows, so they're excluded
            // outright rather than treated as "visible to everyone."
            return members.Where(m => m.BranchId == branchId)
                .Concat(invitationRows.Where(i => i.BranchId == branchId))
                .OrderBy(m => m.Kind == StaffMemberKind.Invitation)
                .ThenBy(m => (m.FirstName + m.LastName + m.Email).ToLowerInvariant())
                .ToList();
        }

        return members.Concat(invitationRows)
            .OrderBy(m => m.Kind == StaffMemberKind.Invitation)
            .ThenBy(m => (m.FirstName + m.LastName + m.Email).ToLowerInvariant())
            .ToList();
    }

    public async Task<StaffMemberDto> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var (user, login) = await LoadMemberAsync(userId, cancellationToken);
        return await ToDtoAsync(user, login, cancellationToken);
    }

    public async Task<StaffInvitationResultDto> InviteAsync(InviteStaffRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        await _inviteValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var role = ParseAssignableRole(request.Role);
        var email = User.NormalizeEmail(request.Email);

        if (await _platform.PlatformUserLogins.AnyAsync(l => l.EmailNormalized == email, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.StaffEmailInUse, "That email already has a Negosio account.");
        }

        var branchId = await ResolveInvitationBranchAsync(role, request.BranchId, cancellationToken);

        var now = UtcNow;
        var existing = await _platform.StaffInvitations
            .SingleOrDefaultAsync(i =>
                i.TenantId == tenantId && i.EmailNormalized == email
                && i.AcceptedAtUtc == null && i.RevokedAtUtc == null, cancellationToken);

        var token = InvitationToken.Generate();
        var expiresAt = now.AddDays(InvitationLifetimeDays);

        StaffInvitation invitation;
        if (existing is not null)
        {
            if (existing.IsPending(now))
            {
                throw new ConflictException(ErrorCodes.StaffAlreadyInvited, "There is already a pending invitation for this email.");
            }

            // An expired invitation is re-sent with a fresh token, role and branch from this request.
            existing.Reissue(token.Hash, expiresAt, now, role, branchId);
            invitation = existing;
        }
        else
        {
            invitation = StaffInvitation.Create(tenantId, email, role, token.Hash, expiresAt, _currentUser.UserId, branchId);
            _platform.StaffInvitations.Add(invitation);
        }

        await SavePlatformOrConflictAsync(cancellationToken);
        return BuildResult(invitation, token.Raw, logNew: true);
    }

    public async Task<StaffInvitationResultDto> ResendInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var invitation = await _platform.StaffInvitations
            .SingleOrDefaultAsync(i => i.Id == invitationId && i.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.InvitationNotFound, "Invitation not found.");

        var now = UtcNow;
        if (invitation.AcceptedAtUtc is not null || invitation.RevokedAtUtc is not null)
        {
            throw new BusinessRuleException(ErrorCodes.InvitationInvalid, "This invitation can no longer be resent.");
        }

        var token = InvitationToken.Generate();
        invitation.Reissue(token.Hash, now.AddDays(InvitationLifetimeDays), now, invitation.Role, invitation.BranchId);
        await _platform.SaveChangesAsync(cancellationToken);
        return BuildResult(invitation, token.Raw, logNew: false);
    }

    public async Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var invitation = await _platform.StaffInvitations
            .SingleOrDefaultAsync(i => i.Id == invitationId && i.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.InvitationNotFound, "Invitation not found.");

        try
        {
            invitation.Revoke(UtcNow);
        }
        catch (InvalidOperationException)
        {
            throw new BusinessRuleException(ErrorCodes.InvitationInvalid, "This invitation has already been accepted.");
        }

        await _platform.SaveChangesAsync(cancellationToken);
    }

    public async Task<StaffMemberDto> ChangeRoleAsync(Guid userId, ChangeStaffRoleRequest request, CancellationToken cancellationToken = default)
    {
        _ = TenantId;
        await _roleValidator.ValidateAndThrowAppAsync(request, cancellationToken);
        var newRole = ParseAssignableRole(request.Role);

        var (user, login) = await LoadMemberAsync(userId, cancellationToken);
        GuardTargetIsNotSelf(userId, "change your own role");
        GuardActingUserMayActOn(login.Role);

        var wasScoped = !BranchRoles.IsAllBranch(login.Role);
        var nowScoped = !BranchRoles.IsAllBranch(newRole);
        var originalBranchId = user.BranchId;

        // Reconcile the branch assignment with the new role (backend invariant).
        Guid? newBranchId = user.BranchId;
        if (!wasScoped && nowScoped)
        {
            newBranchId = await RequireActiveBranchAsync(request.BranchId, cancellationToken);
        }
        else if (wasScoped && nowScoped && !string.IsNullOrWhiteSpace(request.BranchId))
        {
            newBranchId = await RequireActiveBranchAsync(request.BranchId, cancellationToken);
        }
        else if (wasScoped && !nowScoped)
        {
            newBranchId = null;
        }

        if (login.Role == newRole && newBranchId == user.BranchId)
        {
            return await ToDtoAsync(user, login, cancellationToken);
        }

        // Platform first — it is the authority the JWT / OnTokenValidated check reads from.
        var roleChanged = login.Role != newRole;
        if (roleChanged)
        {
            login.ChangeRole(newRole);
            await _platform.SaveChangesAsync(cancellationToken);
            user.ChangeRole(newRole);
        }

        if (newBranchId is { } b)
        {
            user.AssignBranch(b);
        }
        else
        {
            user.ClearBranch();
        }

        // A SalesVoid grant is a direct authority the user's PREVIOUS role/branch holder earned:
        // - moving away from Cashier must not leave it dormant to silently reinstate itself if the
        //   role is later changed back — the next Manager to see them as a Cashier again should have
        //   to re-grant it.
        // - and since the grant has no branch dimension of its own (keyed on UserId, not a
        //   user-in-a-branch), a branch reassignment made through this same endpoint (role staying
        //   Cashier, only request.BranchId changing) must not silently carry the grant to a
        //   destination branch whose Manager never approved it — same rule ChangeBranchAsync applies.
        if ((roleChanged && newRole != UserRole.Cashier) || newBranchId != originalBranchId)
        {
            await RemoveSalesVoidGrantIfAnyAsync(userId, cancellationToken);
        }

        await _tenant.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff {UserId} role changed to {Role} (branch {BranchId}) in tenant {TenantId} by {ActorId}",
            userId, newRole, newBranchId, login.TenantId, _currentUser.UserId);

        return await ToDtoAsync(user, login, cancellationToken);
    }

    public async Task<StaffMemberDto> ChangeBranchAsync(
        Guid userId, ChangeStaffBranchRequest request, CancellationToken cancellationToken = default)
    {
        _ = TenantId;
        await _branchValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var (user, login) = await LoadMemberAsync(userId, cancellationToken);

        if (BranchRoles.IsAllBranch(login.Role))
        {
            throw new BusinessRuleException(ErrorCodes.BranchForbidden, "Owners and admins aren't assigned to a branch.");
        }

        var hasOpenSession = await _tenant.RegisterSessions
            .AnyAsync(s => s.OpenedByUserId == userId && s.Status == RegisterSessionStatus.Open, cancellationToken);
        if (hasOpenSession)
        {
            throw new ConflictException(
                ErrorCodes.StaffHasOpenRegisterSession,
                "Close or force-close this person's open register session before moving them.");
        }

        var branchId = await RequireActiveBranchAsync(request.BranchId, cancellationToken);
        var branchChanged = user.BranchId != branchId;
        user.AssignBranch(branchId);

        // The grant model has no branch dimension of its own (it's keyed on UserId, not a
        // user-in-a-branch) — a SalesVoid grant made by the origin branch's Manager must not silently
        // follow the user to a destination branch whose Manager never approved it. That Manager
        // should decide fresh whether to grant it.
        if (branchChanged)
        {
            await RemoveSalesVoidGrantIfAnyAsync(userId, cancellationToken);
        }

        await _tenant.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff {UserId} moved to branch {BranchId} in tenant {TenantId} by {ActorId}",
            userId, branchId, login.TenantId, _currentUser.UserId);

        return await ToDtoAsync(user, login, cancellationToken);
    }

    public async Task<StaffMemberDto> DeactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var (user, login) = await LoadMemberAsync(userId, cancellationToken);
        GuardTargetIsNotSelf(userId, "deactivate your own account");
        GuardActingUserMayActOn(login.Role);

        if (login.Role == UserRole.Owner)
        {
            var activeOwners = await _platform.PlatformUserLogins
                .CountAsync(l => l.TenantId == tenantId && l.Role == UserRole.Owner && l.IsActive, cancellationToken);
            if (activeOwners <= 1)
            {
                throw new ForbiddenAppException(ErrorCodes.LastOwner, "The tenant's only Owner cannot be deactivated.");
            }
        }

        if (!login.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.StaffAlreadyDeactivated, "This staff member is already deactivated.");
        }

        login.Deactivate();
        await _platform.SaveChangesAsync(cancellationToken);
        user.Deactivate();
        await _tenant.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff {UserId} deactivated in tenant {TenantId} by {ActorId}",
            userId, tenantId, _currentUser.UserId);

        return await ToDtoAsync(user, login, cancellationToken);
    }

    public async Task<StaffMemberDto> ReactivateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var tenantId = TenantId;
        var (user, login) = await LoadMemberAsync(userId, cancellationToken);
        GuardActingUserMayActOn(login.Role);

        if (login.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.StaffAlreadyActive, "This staff member is already active.");
        }

        login.Reactivate();
        await _platform.SaveChangesAsync(cancellationToken);
        user.Reactivate();
        await _tenant.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff {UserId} reactivated in tenant {TenantId} by {ActorId}",
            userId, tenantId, _currentUser.UserId);

        return await ToDtoAsync(user, login, cancellationToken);
    }

    // ---- helpers ----

    /// <summary>
    /// System-driven cleanup for a role/branch change already authorized by the caller reaching this
    /// point — this deliberately bypasses <see cref="ISalesVoidPermissionService.SetAsync"/>, which
    /// checks the ACTING user's authority to grant/revoke, since that check doesn't apply here (no
    /// new grant/revoke is being requested; an existing one is just being invalidated by the change
    /// that already happened). Queued on the same tracked <see cref="ITenantDbContext"/> the caller
    /// saves right after, so it commits atomically with the role/branch change itself.
    /// </summary>
    private async Task RemoveSalesVoidGrantIfAnyAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;
        var grants = await _tenant.UserPermissionGrants
            .Where(g => g.TenantId == tenantId && g.UserId == userId && g.Permission == UserPermission.SalesVoid)
            .ToListAsync(cancellationToken);

        if (grants.Count > 0)
        {
            _tenant.UserPermissionGrants.RemoveRange(grants);
        }
    }

    private async Task<(User User, PlatformUserLogin Login)> LoadMemberAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;

        var user = await _tenant.Users
            .SingleOrDefaultAsync(u => u.TenantId == tenantId && u.Id == userId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

        var login = await _platform.PlatformUserLogins
            .SingleOrDefaultAsync(l => l.Id == userId && l.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

        // Same strict scoping as ListAsync's branch filter: a non-all-branch caller (Manager) only
        // ever sees BranchId == their own assigned branch — never a different branch, and never a null
        // BranchId (which always means Owner/Admin, Phase 5 invariant). Reported as "not found" rather
        // than "forbidden", matching this method's own not-found idiom above and ReturnService's
        // GuardSaleBranchAsync pattern elsewhere in the codebase — it doesn't confirm to an
        // unauthorized caller that a given id exists in some other branch. Every write path that
        // reaches here is Owner/Admin-only at the controller (StaffManage), so this is a no-op for
        // them; only the read path (StaffView, which also admits Manager) is actually constrained.
        var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } branchId && user.BranchId != branchId)
        {
            throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");
        }

        return (user, login);
    }

    private void GuardTargetIsNotSelf(Guid targetUserId, string action)
    {
        if (targetUserId == _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.StaffSelfAction, $"You cannot {action}.");
        }
    }

    /// <summary>An Admin may not act on an Owner; only an Owner may act on an Owner (and that path is
    /// otherwise blocked by self-action / last-owner rules, so it never actually deactivates the Owner).</summary>
    private void GuardActingUserMayActOn(UserRole targetRole)
    {
        if (targetRole == UserRole.Owner && _currentUser.Role != UserRole.Owner)
        {
            throw new ForbiddenAppException(ErrorCodes.OwnerProtected, "Only an Owner can manage an Owner account.");
        }
    }

    private UserRole ParseAssignableRole(string value)
    {
        if (!Enum.TryParse<UserRole>(value, ignoreCase: true, out var role) || !Enum.IsDefined(role))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["role"] = new[] { $"'{value}' is not a valid role." }
            });
        }

        if (role == UserRole.Owner)
        {
            throw new ForbiddenAppException(ErrorCodes.OwnerRoleForbidden, "The Owner role cannot be assigned.");
        }

        if (!StaffRoles.CanAssign(_currentUser.Role, role))
        {
            throw new ForbiddenAppException(ErrorCodes.RoleNotAssignable, $"You are not allowed to assign the {role} role.");
        }

        return role;
    }

    private async Task SavePlatformOrConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _platform.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            throw new ConflictException(ErrorCodes.StaffAlreadyInvited, "There is already a pending invitation for this email.");
        }
    }

    private StaffInvitationResultDto BuildResult(StaffInvitation invitation, string rawToken, bool logNew)
    {
        var acceptPath = $"/invite/{rawToken}";
        _logger.LogInformation(
            "Staff invitation {InvitationId} ({Action}) for {Email} ({Role}) in tenant {TenantId}. Accept at {AcceptPath}",
            invitation.Id, logNew ? "created" : "resent", invitation.EmailNormalized, invitation.Role,
            invitation.TenantId, acceptPath);

        return new StaffInvitationResultDto(
            invitation.Id, invitation.EmailNormalized, invitation.Role, invitation.ExpiresAtUtc,
            AcceptPath: _environment.IsProduction ? null : acceptPath);
    }

    private async Task<StaffMemberDto> ToDtoAsync(User user, PlatformUserLogin login, CancellationToken cancellationToken)
    {
        string? branchName = null;
        if (user.BranchId is { } branchId)
        {
            branchName = await _tenant.Branches.AsNoTracking()
                .Where(b => b.Id == branchId).Select(b => b.Name).FirstOrDefaultAsync(cancellationToken);
        }

        var salesVoid = login.Role == UserRole.Cashier
            && await _tenant.UserPermissionGrants.AsNoTracking()
                .AnyAsync(g => g.TenantId == login.TenantId && g.UserId == user.Id && g.Permission == UserPermission.SalesVoid, cancellationToken);

        return new StaffMemberDto(
            user.Id, StaffMemberKind.Member, user.FirstName, user.LastName, user.Email, login.Role,
            login.IsActive ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
            JoinedAtUtc: user.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null,
            BranchId: user.BranchId, BranchName: branchName, SalesVoid: salesVoid);
    }

    /// <summary>Branch-scoped role → an active branch id is required; Owner/Admin → must be null.</summary>
    private async Task<Guid?> ResolveInvitationBranchAsync(UserRole role, string? branchId, CancellationToken cancellationToken)
    {
        if (BranchRoles.IsAllBranch(role))
        {
            if (!string.IsNullOrWhiteSpace(branchId))
            {
                throw new BusinessRuleException(ErrorCodes.BranchForbidden, "Owners and admins aren't assigned to a branch.");
            }

            return null;
        }

        return await RequireActiveBranchAsync(branchId, cancellationToken);
    }

    private async Task<Guid> RequireActiveBranchAsync(string? branchId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(branchId, out var id))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["branchId"] = new[] { "A branch is required for this role." }
            });
        }

        var active = await _tenant.Branches.AnyAsync(
            b => b.TenantId == _currentUser.TenantId && b.Id == id && b.IsActive, cancellationToken);
        if (!active)
        {
            throw new BusinessRuleException(ErrorCodes.BranchInactive, "That branch is inactive or does not exist.");
        }

        return id;
    }
}
