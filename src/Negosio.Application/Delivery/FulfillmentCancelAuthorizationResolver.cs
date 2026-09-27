using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Common;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

/// <summary>
/// The single "can this actor cancel/convert a pending delivery or pickup schedule right now"
/// resolution — same shape as <see cref="IVoidAuthorizationResolver"/>/<see cref="IReturnAuthorizationResolver"/>:
/// Owner/Admin/Manager act directly; a Cashier needs the FulfillmentCancel grant or a verified
/// Manager/Admin/Owner approval. Kept as its own resolver (rather than folded into one of the others)
/// for the same reason Return is its own copy rather than reusing Void's.
/// </summary>
public interface IFulfillmentCancelAuthorizationResolver
{
    /// <summary>Returns the approving Manager/Admin/Owner's user id when approval was actually needed
    /// and verified; null when the requester acted directly (a privileged role, or a Cashier with the
    /// FulfillmentCancel grant) — the caller persists this on <see cref="Domain.Entities.DeliveryReceipt.ApprovedByUserId"/>.</summary>
    Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default);
}

public sealed class FulfillmentCancelAuthorizationResolver : IFulfillmentCancelAuthorizationResolver
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserPermissionGrantService _permissions;
    private readonly IApproverVerificationService _approverVerification;

    public FulfillmentCancelAuthorizationResolver(
        ICurrentUser currentUser, IUserPermissionGrantService permissions, IApproverVerificationService approverVerification)
    {
        _currentUser = currentUser;
        _permissions = permissions;
        _approverVerification = approverVerification;
    }

    public async Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default)
    {
        // Explicit, defense-in-depth role gate — never rely solely on the controller's FulfillmentCancel
        // policy (which now just admits every POS role, the same way SalesView does for Void).
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot cancel a delivery or pickup schedule.");
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return null;
        }

        // role == UserRole.Cashier, explicitly — the guard above already rejected every other role.
        if (await _permissions.HasGrantAsync(_currentUser.UserId, UserPermission.FulfillmentCancel, cancellationToken))
        {
            return null;
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.FulfillmentCancelApprovalRequired,
                "You don't have permission to cancel this schedule. An authorized Manager, Admin, or Owner must approve this cancellation.");
        }

        return await _approverVerification.VerifyAsync(approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
    }
}
