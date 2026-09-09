using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Common;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

/// <summary>
/// The single "can this actor process a return right now" resolution — same shape as
/// <see cref="IVoidAuthorizationResolver"/>: Owner/Admin/Manager act directly; a Cashier needs the
/// SalesReturn grant or a verified Manager/Admin/Owner approval. Kept as its own resolver (rather
/// than folded into <see cref="IVoidAuthorizationResolver"/>) so a change to Void's error codes or
/// messages can never accidentally affect Return, matching the precedent already set by
/// CashDrawerService/CheckoutService each keeping their own copy of this same shape.
/// </summary>
public interface IReturnAuthorizationResolver
{
    /// <summary>Returns the approving Manager/Admin/Owner's user id when approval was actually needed
    /// and verified; null when the requester acted directly (a privileged role, or a Cashier with the
    /// SalesReturn grant) — the caller persists this on <see cref="SaleReturn.ApprovedByUserId"/>.</summary>
    Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default);
}

public sealed class ReturnAuthorizationResolver : IReturnAuthorizationResolver
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserPermissionGrantService _permissions;
    private readonly IApproverVerificationService _approverVerification;

    public ReturnAuthorizationResolver(
        ICurrentUser currentUser, IUserPermissionGrantService permissions, IApproverVerificationService approverVerification)
    {
        _currentUser = currentUser;
        _permissions = permissions;
        _approverVerification = approverVerification;
    }

    public async Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default)
    {
        // Explicit, defense-in-depth role gate — never rely solely on the controller's RefundManage
        // policy (which now just admits every POS role, the same way SalesView does for Void).
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot process returns.");
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return null;
        }

        // role == UserRole.Cashier, explicitly — the guard above already rejected every other role.
        if (await _permissions.HasGrantAsync(_currentUser.UserId, UserPermission.SalesReturn, cancellationToken))
        {
            return null;
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.ReturnApprovalRequired,
                "You don't have permission to process returns. An authorized Manager, Admin, or Owner must approve this return.");
        }

        return await _approverVerification.VerifyAsync(approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
    }
}
