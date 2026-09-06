using FluentValidation;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Sales;

namespace Negosio.Application.Pos;

/// <summary>
/// Authorizes cancelling the cart currently being worked on at the register — a cart that has
/// never been checked out has no Sale row and no SaleNumber, so there is nothing here to void or
/// persist. This exists purely to gate that cancellation behind the same Manager/Admin/Owner
/// approval rule as voiding a completed sale (see <see cref="IVoidAuthorizationResolver"/>), without
/// inventing a fake sale or a reserved document number just to hang a permission check on.
/// </summary>
public sealed record CancelTransactionAuthorizationRequest(Guid? BranchId, VoidSaleApprovalInput? Approval);

public interface ITransactionCancellationService
{
    Task AuthorizeCancelAsync(CancelTransactionAuthorizationRequest request, CancellationToken cancellationToken = default);
}

public sealed class TransactionCancellationService : ITransactionCancellationService
{
    private readonly IValidator<CancelTransactionAuthorizationRequest> _validator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IVoidAuthorizationResolver _authResolver;

    public TransactionCancellationService(
        IValidator<CancelTransactionAuthorizationRequest> validator,
        IBranchAccessResolver branchAccess,
        IVoidAuthorizationResolver authResolver)
    {
        _validator = validator;
        _branchAccess = branchAccess;
        _authResolver = authResolver;
    }

    public async Task AuthorizeCancelAsync(CancelTransactionAuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);
        var branchId = await _branchAccess.ResolveTargetBranchAsync(request.BranchId, cancellationToken: cancellationToken);
        await _authResolver.ResolveAsync(branchId, request.Approval, cancellationToken);
    }
}
