using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Common;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

/// <summary>
/// Resolves whether an actor may void a RestoPOS item that has already been released to the kitchen.
/// Owner/Admin/Manager act directly; a Cashier needs the RestoItemVoid grant or a verified Manager/Admin/Owner
/// approval. Kept as its own resolver, matching the codebase's one-resolver-per-action convention.
/// </summary>
public interface IRestoItemVoidAuthorizationResolver
{
    /// <summary>Returns the approving user's id when approval was needed and verified; null when the
    /// requester acted directly.</summary>
    Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default);
}

public sealed class RestoItemVoidAuthorizationResolver : IRestoItemVoidAuthorizationResolver
{
    private readonly ICurrentUser _currentUser;
    private readonly IUserPermissionGrantService _permissions;
    private readonly IApproverVerificationService _approverVerification;

    public RestoItemVoidAuthorizationResolver(
        ICurrentUser currentUser, IUserPermissionGrantService permissions, IApproverVerificationService approverVerification)
    {
        _currentUser = currentUser;
        _permissions = permissions;
        _approverVerification = approverVerification;
    }

    public async Task<Guid?> ResolveAsync(Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken = default)
    {
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot void a released item.");
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return null;
        }

        if (await _permissions.HasGrantAsync(_currentUser.UserId, UserPermission.RestoItemVoid, cancellationToken))
        {
            return null;
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.RestoItemVoidApprovalRequired,
                "You don't have permission to void a released item. An authorized Manager, Admin, or Owner must approve this.");
        }

        return await _approverVerification.VerifyAsync(approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
    }
}
