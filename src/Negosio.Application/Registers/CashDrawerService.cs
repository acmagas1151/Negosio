using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Common;
using Negosio.Application.Sales;
using Negosio.Application.Staff;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed class CashDrawerService : ICashDrawerService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IUserPermissionGrantService _grants;
    private readonly IApproverVerificationService _approverVerification;

    public CashDrawerService(
        ITenantDbContext db, ICurrentUser currentUser, IUserPermissionGrantService grants,
        IApproverVerificationService approverVerification)
    {
        _db = db;
        _currentUser = currentUser;
        _grants = grants;
        _approverVerification = approverVerification;
    }

    public async Task<CashDrawerOpenDto> OpenAsync(
        Guid sessionId, OpenCashDrawerRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        var session = await _db.RegisterSessions
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        // Same ownership + open-session rules as cash movements (RegisterCashMovementService) — a
        // no-sale drawer open is scoped to the cashier's own active session, same as Cash in/out.
        if (session.OpenedByUserId != _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.CashDrawerNotOwner, "This register session belongs to another user.");
        }

        if (session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.CashDrawerSessionClosed, "This register session is not open.");
        }

        // Authorization resolved before opening a transaction — nothing is written until this
        // succeeds, so a rejected or cancelled approval leaves no trace (Acceptance scenarios 3-4).
        var (requestedBy, approvedBy) = await ResolveAuthorizationAsync(session.BranchId, request.Approval, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Same pessimistic lock RegisterCashMovementService and VoidSaleService take, as the first
        // statement inside the transaction — serializes this against a concurrent session close.
        await _db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {session.Id}")
            .ToListAsync(cancellationToken);

        var currentStatus = await _db.RegisterSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Id == sessionId)
            .Select(s => s.Status)
            .SingleAsync(cancellationToken);
        if (currentStatus != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.CashDrawerSessionClosed, "This register session is not open.");
        }

        var evt = CashDrawerOpenEvent.Create(tenantId, session.BranchId, session.Id, requestedBy, approvedBy);
        _db.CashDrawerOpenEvents.Add(evt);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ToDtoAsync(evt, cancellationToken);
    }

    /// <summary>
    /// Owner/Admin/Manager act directly; a Cashier needs the CashDrawerOpen grant or a verified
    /// Manager/Admin/Owner approval. Structurally the same as VoidAuthorizationResolver, kept
    /// separate so drawer-specific error codes/messages never get confused with Void's.
    /// </summary>
    private async Task<(Guid RequestedBy, Guid? ApprovedBy)> ResolveAuthorizationAsync(
        Guid branchId, VoidSaleApprovalInput? approval, CancellationToken cancellationToken)
    {
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot open the cash drawer.");
        }

        if (_currentUser.Role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            return (_currentUser.UserId, null);
        }

        if (await _grants.HasGrantAsync(_currentUser.UserId, UserPermission.CashDrawerOpen, cancellationToken))
        {
            return (_currentUser.UserId, null);
        }

        if (approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.CashDrawerApprovalRequired,
                "You don't have permission to open the cash drawer. An authorized Manager, Admin, or Owner must approve this.");
        }

        var approverId = await _approverVerification.VerifyAsync(
            approval.ApproverEmail, approval.ApproverPassword, branchId, cancellationToken);
        return (_currentUser.UserId, approverId);
    }

    private async Task<CashDrawerOpenDto> ToDtoAsync(CashDrawerOpenEvent evt, CancellationToken cancellationToken)
    {
        var userIds = new List<Guid> { evt.RequestedByUserId };
        if (evt.ApprovedByUserId is { } approverId) userIds.Add(approverId);

        var names = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim(), cancellationToken);

        return new CashDrawerOpenDto(
            evt.Id, evt.RegisterSessionId, evt.RequestedByUserId,
            names.GetValueOrDefault(evt.RequestedByUserId, string.Empty),
            evt.ApprovedByUserId,
            evt.ApprovedByUserId is { } id ? names.GetValueOrDefault(id) : null,
            evt.CreatedAtUtc);
    }
}
