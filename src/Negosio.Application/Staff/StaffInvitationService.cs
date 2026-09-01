using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

/// <summary>
/// The public side of staff onboarding: preview and accept an invitation. Runs before the invitee
/// has any JWT, so the tenant is resolved from the token (platform database) and the tenant
/// database is opened directly via the factory — the same principle as login.
/// </summary>
public sealed class StaffInvitationService : IStaffInvitationService
{
    private readonly IPlatformDbContext _platform;
    private readonly ITenantDbContextFactory _tenantFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IValidator<AcceptInvitationRequest> _validator;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<StaffInvitationService> _logger;

    public StaffInvitationService(
        IPlatformDbContext platform,
        ITenantDbContextFactory tenantFactory,
        IPasswordHasher passwordHasher,
        IValidator<AcceptInvitationRequest> validator,
        TimeProvider timeProvider,
        ILogger<StaffInvitationService> logger)
    {
        _platform = platform;
        _tenantFactory = tenantFactory;
        _passwordHasher = passwordHasher;
        _validator = validator;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<InvitationPreviewDto> PreviewAsync(string token, CancellationToken cancellationToken = default)
    {
        var invitation = await FindPendingAsync(token, cancellationToken);
        await EnsureEmailStillFreeAsync(invitation.EmailNormalized, cancellationToken);

        var businessName = await _platform.Tenants.AsNoTracking()
            .Where(t => t.Id == invitation.TenantId)
            .Select(t => t.Name)
            .SingleAsync(cancellationToken);

        return new InvitationPreviewDto(businessName, invitation.EmailNormalized, invitation.Role, invitation.ExpiresAtUtc);
    }

    public async Task<AcceptInvitationResultDto> AcceptAsync(
        string token, AcceptInvitationRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var hash = InvitationToken.Hash(token);
        var invitation = await _platform.StaffInvitations
            .SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.InvitationNotFound, "This invitation link is not valid.");

        var now = UtcNow;
        var existingLogin = await _platform.PlatformUserLogins
            .SingleOrDefaultAsync(l => l.EmailNormalized == invitation.EmailNormalized, cancellationToken);

        // Recovery: a previous accept created the platform login (and marked the invitation accepted)
        // but the tenant-profile write did not complete. Let the same person finish it.
        if (invitation.AcceptedAtUtc is not null)
        {
            if (existingLogin is null)
            {
                throw new BusinessRuleException(ErrorCodes.InvitationInvalid, "This invitation link is no longer valid.");
            }

            var completed = await TenantUserExistsAsync(invitation.TenantId, existingLogin.Id, cancellationToken);
            if (completed)
            {
                throw new ConflictException(ErrorCodes.InvitationAlreadyAccepted, "This invitation has already been used.");
            }

            await CreateTenantUserAsync(invitation.TenantId, existingLogin.Id, invitation.EmailNormalized,
                request.FirstName, request.LastName, existingLogin.Role, invitation.BranchId, cancellationToken);

            _logger.LogInformation("Staff invitation {InvitationId} completed (recovery) for user {UserId}",
                invitation.Id, existingLogin.Id);
            return new AcceptInvitationResultDto(invitation.EmailNormalized);
        }

        if (!invitation.IsPending(now))
        {
            throw new BusinessRuleException(ErrorCodes.InvitationInvalid, "This invitation link has expired or is no longer valid.");
        }

        if (existingLogin is not null)
        {
            throw new ConflictException(ErrorCodes.StaffEmailInUse, "This email is already registered.");
        }

        var userId = Guid.NewGuid();
        var passwordHash = _passwordHasher.Hash(request.Password);

        // Platform is the authentication + tenant-routing authority: create it first and mark the
        // invitation accepted in one platform save. If the tenant write then fails, a retry lands
        // in the recovery branch above.
        _platform.PlatformUserLogins.Add(
            PlatformUserLogin.Create(userId, invitation.TenantId, invitation.EmailNormalized, passwordHash, invitation.Role));
        invitation.Accept(now);

        try
        {
            await _platform.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            throw new ConflictException(ErrorCodes.StaffEmailInUse, "This email is already registered.");
        }

        await CreateTenantUserAsync(invitation.TenantId, userId, invitation.EmailNormalized,
            request.FirstName, request.LastName, invitation.Role, invitation.BranchId, cancellationToken);

        _logger.LogInformation(
            "Staff invitation {InvitationId} accepted: user {UserId} ({Role}) joined tenant {TenantId}",
            invitation.Id, userId, invitation.Role, invitation.TenantId);

        return new AcceptInvitationResultDto(invitation.EmailNormalized);
    }

    private async Task<StaffInvitation> FindPendingAsync(string token, CancellationToken cancellationToken)
    {
        var hash = InvitationToken.Hash(token);
        var invitation = await _platform.StaffInvitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.TokenHash == hash, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.InvitationNotFound, "This invitation link is not valid.");

        if (!invitation.IsPending(UtcNow))
        {
            throw new BusinessRuleException(ErrorCodes.InvitationInvalid, "This invitation link has expired or is no longer valid.");
        }

        return invitation;
    }

    private async Task EnsureEmailStillFreeAsync(string emailNormalized, CancellationToken cancellationToken)
    {
        if (await _platform.PlatformUserLogins.AnyAsync(l => l.EmailNormalized == emailNormalized, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.StaffEmailInUse, "This email is already registered.");
        }
    }

    private async Task<bool> TenantUserExistsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        await using var db = await _tenantFactory.CreateAsync(tenantId, cancellationToken);
        return await db.Users.AnyAsync(u => u.Id == userId, cancellationToken);
    }

    private async Task CreateTenantUserAsync(
        Guid tenantId, Guid userId, string email, string firstName, string lastName,
        UserRole role, Guid? branchId, CancellationToken cancellationToken)
    {
        await using var db = await _tenantFactory.CreateAsync(tenantId, cancellationToken);
        if (await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            return;
        }

        db.Users.Add(User.Create(userId, tenantId, email, firstName, lastName, role, branchId));
        await db.SaveChangesAsync(cancellationToken);
    }
}
