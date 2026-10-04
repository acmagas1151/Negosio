using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Common;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

/// <summary>
/// The single "can this actor record a cash in/out movement right now" resolution — same shape as
/// <see cref="Delivery.IFulfillmentCancelAuthorizationResolver"/>: Owner/Admin/Manager act directly; a
/// Cashier needs the CashMovement grant or a verified Manager/Admin/Owner approval. Kept as its own
/// resolver (rather than reusing <see cref="Pos.ICashDrawerService"/>'s own copy of this shape) since
/// opening the drawer and recording a cash movement are two distinct grantable permissions, matching
/// the precedent already set by Void/Return/Discount each keeping their own copy.
/// </summary>
public interface ICashMovementAuthorizationResolver
{
    /// <summary>Returns the approving Manager/Admin/Owner's user id when approval was actually needed
    /// and verified; null when the requester acted directly (a privileged role, or a Cashier with the
    /// CashMovement grant) — the caller persists this on <see cref="Domain.Entities.RegisterCashMovement.ApprovedByUserId"/>.</summary>
    Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default);
}

public sealed class CashMovementAuthorizationResolver : ICashMovementAuthorizationResolver
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserPermissionGrantService _permissions;
    private readonly IApproverVerificationService _approverVerification;

    public CashMovementAuthorizationResolver(
        ICurrentUser currentUser, IUserPermissionGrantService permissions, IApproverVerificationService approverVerification)
    {
        _currentUser = currentUser;
        _permissions = permissions;
        _approverVerification = approverVerification;
    }

    public async Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default)
    {
        // Explicit, defense-in-depth role gate — never rely solely on the controller's PosOperate
        // policy (which admits every POS role, the same way SalesView does for Void).
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot record a cash movement.");
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return null;
        }

        // role == UserRole.Cashier, explicitly — the guard above already rejected every other role.
        if (await _permissions.HasGrantAsync(_currentUser.UserId, UserPermission.CashMovement, cancellationToken))
        {
            return null;
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.CashMovementApprovalRequired,
                "You don't have permission to record cash movements. An authorized Manager, Admin, or Owner must approve this.");
        }

        return await _approverVerification.VerifyAsync(approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
    }
}
