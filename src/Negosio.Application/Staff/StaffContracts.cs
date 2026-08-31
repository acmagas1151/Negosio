using Negosio.Domain.Enums;

namespace Negosio.Application.Staff;

/// <summary>What a staff-roster row is: an accepted user, or a still-open invitation.</summary>
public enum StaffMemberKind
{
    Member = 1,
    Invitation = 2
}

public enum StaffMemberStatus
{
    Active = 1,
    Deactivated = 2,
    Invited = 3,
    Expired = 4
}

/// <summary>
/// One row of the staff roster. For an accepted member, <see cref="Id"/> is the user id and the
/// name fields are populated. For an invitation, <see cref="Id"/> is the invitation id and the name
/// is not yet known. Never carries password or token hashes.
/// </summary>
public sealed record StaffMemberDto(
    Guid Id,
    StaffMemberKind Kind,
    string? FirstName,
    string? LastName,
    string Email,
    UserRole Role,
    StaffMemberStatus Status,
    DateTime? JoinedAtUtc,
    DateTime? InvitedAtUtc,
    DateTime? ExpiresAtUtc,
    string? InvitedByName);

public sealed record InviteStaffRequest(string Email, string Role);

public sealed record ChangeStaffRoleRequest(string Role);

/// <summary>
/// Returned after creating / resending an invitation. <see cref="AcceptPath"/> is populated only
/// in the Development environment (no outbound email yet) — it is always <c>null</c> otherwise.
/// </summary>
public sealed record StaffInvitationResultDto(
    Guid InvitationId,
    string Email,
    UserRole Role,
    DateTime ExpiresAtUtc,
    string? AcceptPath);

public interface IStaffService
{
    Task<IReadOnlyList<StaffMemberDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<StaffMemberDto> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<StaffInvitationResultDto> InviteAsync(InviteStaffRequest request, CancellationToken cancellationToken = default);

    Task<StaffInvitationResultDto> ResendInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);

    Task RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken = default);

    Task<StaffMemberDto> ChangeRoleAsync(Guid userId, ChangeStaffRoleRequest request, CancellationToken cancellationToken = default);

    Task<StaffMemberDto> DeactivateAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<StaffMemberDto> ReactivateAsync(Guid userId, CancellationToken cancellationToken = default);
}

// ---- Public invitation acceptance (no tenant JWT yet) ----

public sealed record InvitationPreviewDto(
    string BusinessName,
    string Email,
    UserRole Role,
    DateTime ExpiresAtUtc);

public sealed record AcceptInvitationRequest(string FirstName, string LastName, string Password);

public sealed record AcceptInvitationResultDto(string Email);

public interface IStaffInvitationService
{
    Task<InvitationPreviewDto> PreviewAsync(string token, CancellationToken cancellationToken = default);

    Task<AcceptInvitationResultDto> AcceptAsync(string token, AcceptInvitationRequest request, CancellationToken cancellationToken = default);
}
