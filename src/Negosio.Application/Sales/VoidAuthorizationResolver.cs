using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Common;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

/// <summary>
/// The single "can this actor void/cancel right now" resolution — Owner/Admin/Manager act directly;
/// a Cashier needs the SalesVoid grant or a verified Manager/Admin/Owner approval. Shared by
/// <see cref="VoidSaleService"/> (voiding a persisted sale) and the POS terminal's
/// "cancel the uncompleted cart" authorization, so the two never drift into two copies of the same
/// permission rule. Branch-scoped only by the caller passing the right <c>branchId</c> — this itself
/// has no notion of a Sale.
/// </summary>
public interface IVoidAuthorizationResolver
{
    Task<(Guid ActorUserId, Guid? ApproverUserId)> ResolveAsync(
        Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default);
}

public sealed class VoidAuthorizationResolver : IVoidAuthorizationResolver
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserPermissionGrantService _permissions;
    private readonly IApproverVerificationService _approverVerification;

    public VoidAuthorizationResolver(
        ICurrentUser currentUser, IUserPermissionGrantService permissions, IApproverVerificationService approverVerification)
    {
        _currentUser = currentUser;
        _permissions = permissions;
        _approverVerification = approverVerification;
    }

    public async Task<(Guid ActorUserId, Guid? ApproverUserId)> ResolveAsync(
        Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default)
    {
        // Explicit, defense-in-depth role gate — never rely solely on a controller policy.
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot void sales.");
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return (_currentUser.UserId, null);
        }

        // role == UserRole.Cashier, explicitly — the guard above already rejected every other role.
        if (await _permissions.HasGrantAsync(_currentUser.UserId, UserPermission.SalesVoid, cancellationToken))
        {
            return (_currentUser.UserId, null);
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.VoidApprovalRequired,
                "You don't have permission to void completed sales. An authorized Manager, Admin, or Owner must approve this void.");
        }

        var approverId = await _approverVerification.VerifyAsync(
            approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
        return (_currentUser.UserId, approverId);
    }
}
