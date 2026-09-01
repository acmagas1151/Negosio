using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>Lifecycle state of a <see cref="StaffInvitation"/>. Computed from timestamps — not persisted.</summary>
public enum StaffInvitationStatus
{
    Pending = 1,
    Accepted = 2,
    Revoked = 3,
    Expired = 4
}

/// <summary>
/// A pending invitation for someone to join a tenant as staff (platform database — the same place
/// that resolves an email to its tenant, so acceptance works before the invitee has any JWT).
/// Only a hash of the single-use token is stored; the raw token is shown once at creation.
/// One email = one tenant still holds: an email that already has a platform login cannot be invited.
/// </summary>
public class StaffInvitation : Entity
{
    private StaffInvitation()
    {
        EmailNormalized = string.Empty;
        TokenHash = string.Empty;
    }

    private StaffInvitation(
        Guid tenantId, string emailNormalized, UserRole role, string tokenHash, DateTime expiresAtUtc,
        Guid invitedByUserId, Guid? branchId)
    {
        TenantId = tenantId;
        EmailNormalized = emailNormalized;
        Role = role;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        InvitedByUserId = invitedByUserId;
        BranchId = branchId;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Trimmed, lower-cased email the invitation is bound to.</summary>
    public string EmailNormalized { get; private set; }

    public UserRole Role { get; private set; }

    /// <summary>Hash (hex SHA-256) of the single-use token. The raw token is never stored.</summary>
    public string TokenHash { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public DateTime? AcceptedAtUtc { get; private set; }

    public DateTime? RevokedAtUtc { get; private set; }

    public Guid InvitedByUserId { get; private set; }

    /// <summary>Target branch for a branch-scoped role; null for Owner/Admin. Validated by the caller.</summary>
    public Guid? BranchId { get; private set; }

    public static StaffInvitation Create(
        Guid tenantId, string emailNormalized, UserRole role, string tokenHash, DateTime expiresAtUtc,
        Guid invitedByUserId, Guid? branchId = null)
    {
        if (string.IsNullOrWhiteSpace(emailNormalized))
        {
            throw new ArgumentException("Email is required.", nameof(emailNormalized));
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            throw new ArgumentException("Token hash is required.", nameof(tokenHash));
        }

        if (role == UserRole.Owner)
        {
            throw new InvalidOperationException("Staff cannot be invited as Owner.");
        }

        return new StaffInvitation(tenantId, emailNormalized, role, tokenHash, expiresAtUtc, invitedByUserId, branchId);
    }

    public StaffInvitationStatus StatusAt(DateTime nowUtc) =>
        RevokedAtUtc is not null ? StaffInvitationStatus.Revoked
        : AcceptedAtUtc is not null ? StaffInvitationStatus.Accepted
        : nowUtc >= ExpiresAtUtc ? StaffInvitationStatus.Expired
        : StaffInvitationStatus.Pending;

    public bool IsPending(DateTime nowUtc) => StatusAt(nowUtc) == StaffInvitationStatus.Pending;

    public void Accept(DateTime nowUtc)
    {
        if (!IsPending(nowUtc))
        {
            throw new InvalidOperationException($"This invitation is {StatusAt(nowUtc)} and cannot be accepted.");
        }

        AcceptedAtUtc = nowUtc;
        Touch();
    }

    public void Revoke(DateTime nowUtc)
    {
        if (AcceptedAtUtc is not null)
        {
            throw new InvalidOperationException("An accepted invitation cannot be revoked.");
        }

        RevokedAtUtc ??= nowUtc;
        Touch();
    }

    /// <summary>
    /// Issue a fresh token + expiry (and role + branch) for a resend / re-invite. Only valid while
    /// the invitation is still pending or has expired. Callers pass the current values to keep them.
    /// </summary>
    public void Reissue(
        string newTokenHash, DateTime newExpiresAtUtc, DateTime nowUtc, UserRole role, Guid? branchId)
    {
        if (!IsPending(nowUtc) && StatusAt(nowUtc) != StaffInvitationStatus.Expired)
        {
            throw new InvalidOperationException($"This invitation is {StatusAt(nowUtc)} and cannot be resent.");
        }

        if (string.IsNullOrWhiteSpace(newTokenHash))
        {
            throw new ArgumentException("Token hash is required.", nameof(newTokenHash));
        }

        if (role == UserRole.Owner)
        {
            throw new InvalidOperationException("Staff cannot be invited as Owner.");
        }

        TokenHash = newTokenHash;
        ExpiresAtUtc = newExpiresAtUtc;
        Role = role;
        BranchId = branchId;
        Touch();
    }
}
