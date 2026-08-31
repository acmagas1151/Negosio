using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
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

    public StaffService(
        IPlatformDbContext platform,
        ITenantDbContext tenant,
        ICurrentUser currentUser,
        IValidator<InviteStaffRequest> inviteValidator,
        IValidator<ChangeStaffRoleRequest> roleValidator,
        IAppEnvironment environment,
        TimeProvider timeProvider,
        ILogger<StaffService> logger)
    {
        _platform = platform;
        _tenant = tenant;
        _currentUser = currentUser;
        _inviteValidator = inviteValidator;
        _roleValidator = roleValidator;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
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
            .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email, u.Role, u.IsActive, u.CreatedAtUtc })
            .ToListAsync(cancellationToken);

        var userIds = users.Select(u => u.Id).ToList();
        var logins = await _platform.PlatformUserLogins.AsNoTracking()
            .Where(l => l.TenantId == tenantId && userIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => new { l.Role, l.IsActive }, cancellationToken);

        var names = users.ToDictionary(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim());

        var members = users.Select(u =>
        {
            // Platform is the authority for role + active state; fall back to the tenant row.
            var role = logins.TryGetValue(u.Id, out var l) ? l.Role : u.Role;
            var active = logins.TryGetValue(u.Id, out var l2) ? l2.IsActive : u.IsActive;
            return new StaffMemberDto(
                u.Id, StaffMemberKind.Member, u.FirstName, u.LastName, u.Email, role,
                active ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
                JoinedAtUtc: u.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null);
        });

        var now = UtcNow;
        var invitations = await _platform.StaffInvitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.AcceptedAtUtc == null && i.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);

        var invitationRows = invitations.Select(i => new StaffMemberDto(
            i.Id, StaffMemberKind.Invitation, null, null, i.EmailNormalized, i.Role,
            now >= i.ExpiresAtUtc ? StaffMemberStatus.Expired : StaffMemberStatus.Invited,
            JoinedAtUtc: null, InvitedAtUtc: i.CreatedAtUtc, ExpiresAtUtc: i.ExpiresAtUtc,
            InvitedByName: names.GetValueOrDefault(i.InvitedByUserId)));

        return members.Concat(invitationRows)
            .OrderBy(m => m.Kind == StaffMemberKind.Invitation)
            .ThenBy(m => (m.FirstName + m.LastName + m.Email).ToLowerInvariant())
            .ToList();
    }

    public async Task<StaffMemberDto> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var (user, login) = await LoadMemberAsync(userId, cancellationToken);
        return ToDto(user, login);
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

            // An expired invitation is re-sent with a fresh token; to change its role, revoke and re-invite.
            existing.Reissue(token.Hash, expiresAt, now);
            invitation = existing;
        }
        else
        {
            invitation = StaffInvitation.Create(tenantId, email, role, token.Hash, expiresAt, _currentUser.UserId);
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
        invitation.Reissue(token.Hash, now.AddDays(InvitationLifetimeDays), now);
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

        if (login.Role == newRole)
        {
            return ToDto(user, login);
        }

        // Platform first — it is the authority the JWT / OnTokenValidated check reads from.
        login.ChangeRole(newRole);
        await _platform.SaveChangesAsync(cancellationToken);

        user.ChangeRole(newRole);
        await _tenant.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Staff {UserId} role changed to {Role} in tenant {TenantId} by {ActorId}",
            userId, newRole, login.TenantId, _currentUser.UserId);

        return ToDto(user, login);
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

        return ToDto(user, login);
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

        return ToDto(user, login);
    }

    // ---- helpers ----

    private async Task<(User User, PlatformUserLogin Login)> LoadMemberAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tenantId = TenantId;

        var user = await _tenant.Users
            .SingleOrDefaultAsync(u => u.TenantId == tenantId && u.Id == userId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

        var login = await _platform.PlatformUserLogins
            .SingleOrDefaultAsync(l => l.Id == userId && l.TenantId == tenantId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.StaffNotFound, "Staff member not found.");

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

    private static StaffMemberDto ToDto(User user, PlatformUserLogin login) => new(
        user.Id, StaffMemberKind.Member, user.FirstName, user.LastName, user.Email, login.Role,
        login.IsActive ? StaffMemberStatus.Active : StaffMemberStatus.Deactivated,
        JoinedAtUtc: user.CreatedAtUtc, InvitedAtUtc: null, ExpiresAtUtc: null, InvitedByName: null);
}
