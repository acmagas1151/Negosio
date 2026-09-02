using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Auth;

public interface IApproverVerificationService
{
    /// <summary>
    /// Reverifies a Manager/Admin/Owner's password without issuing a JWT. Returns the approver's
    /// tenant User id on success. One-shot — nothing about this call is cached or reusable for a
    /// later request.
    /// </summary>
    Task<Guid> VerifyAsync(string approverEmail, string approverPassword, Guid saleBranchId, CancellationToken cancellationToken = default);
}

public sealed class ApproverVerificationService : IApproverVerificationService
{
    private readonly IPlatformDbContext _platform;
    private readonly ITenantDbContext _tenant;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ICurrentUser _currentUser;
    private Lazy<string>? _timingEqualizerHash;

    public ApproverVerificationService(
        IPlatformDbContext platform, ITenantDbContext tenant, IPasswordHasher passwordHasher, ICurrentUser currentUser)
    {
        _platform = platform;
        _tenant = tenant;
        _passwordHasher = passwordHasher;
        _currentUser = currentUser;
    }

    public async Task<Guid> VerifyAsync(
        string approverEmail, string approverPassword, Guid saleBranchId, CancellationToken cancellationToken = default)
    {
        var normalizedEmail = User.NormalizeEmail(approverEmail);
        var login = await _platform.PlatformUserLogins
            .SingleOrDefaultAsync(l => l.EmailNormalized == normalizedEmail, cancellationToken);

        // Same timing-equalizer approach as AuthService.LoginAsync — verify a hash either way.
        var hashToCheck = login?.PasswordHash ?? GetTimingEqualizerHash();
        var passwordValid = _passwordHasher.Verify(approverPassword, hashToCheck);

        // Also reject a login that resolves to a different tenant — never reveal that distinction.
        if (login is null || !passwordValid || login.TenantId != _currentUser.TenantId || !login.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidApproverCredentials, "Invalid manager credentials.");
        }

        var approver = await _tenant.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.TenantId == _currentUser.TenantId && u.Id == login.Id, cancellationToken);
        if (approver is null || !approver.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.InvalidApproverCredentials, "Invalid manager credentials.");
        }

        if (approver.Role is not (UserRole.Manager or UserRole.Admin or UserRole.Owner))
        {
            // A 400, not 403: the *acting* user (the Cashier submitting these credentials) is not
            // the one lacking permission here — the *submitted account* simply cannot serve as an
            // approver. That is a defect in the request, not a forbidden action by the caller.
            throw new BusinessRuleException(ErrorCodes.VoidApproverNotAuthorized, "This account cannot approve voids.");
        }

        if (approver.Role == UserRole.Manager)
        {
            if (approver.BranchId != saleBranchId)
            {
                throw new BusinessRuleException(ErrorCodes.VoidApproverWrongBranch, "This manager cannot approve voids for this branch.");
            }

            var branchActive = await _tenant.Branches.AsNoTracking()
                .AnyAsync(b => b.Id == saleBranchId && b.IsActive, cancellationToken);
            if (!branchActive)
            {
                throw new BusinessRuleException(ErrorCodes.VoidApproverWrongBranch, "This manager cannot approve voids for this branch.");
            }
        }

        return approver.Id;
    }

    private string GetTimingEqualizerHash() =>
        (_timingEqualizerHash ??= new Lazy<string>(() => _passwordHasher.Hash("timing-equalizer"))).Value;
}
