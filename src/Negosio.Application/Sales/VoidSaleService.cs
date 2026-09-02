using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Inventory;
using Negosio.Application.Staff;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Sales;

public sealed class VoidSaleService : IVoidSaleService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<VoidSaleRequest> _validator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly ISalesVoidPermissionService _permissions;
    private readonly IApproverVerificationService _approverVerification;
    private readonly IInventoryPosting _inventory;
    private readonly ISaleQueryService _saleQuery;
    private readonly TimeProvider _timeProvider;

    public VoidSaleService(
        ITenantDbContext db, ICurrentUser currentUser, IValidator<VoidSaleRequest> validator,
        IBranchAccessResolver branchAccess, ISalesVoidPermissionService permissions,
        IApproverVerificationService approverVerification, IInventoryPosting inventory,
        ISaleQueryService saleQuery, TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _branchAccess = branchAccess;
        _permissions = permissions;
        _approverVerification = approverVerification;
        _inventory = inventory;
        _saleQuery = saleQuery;
        _timeProvider = timeProvider;
    }

    public async Task<SaleDetailDto> VoidAsync(Guid saleId, VoidSaleRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        // Explicit, defense-in-depth role gate — never rely solely on the controller's SalesView
        // policy, and never treat "not Owner/Admin/Manager" as an implicit synonym for Cashier.
        if (_currentUser.Role is not (UserRole.Owner or UserRole.Admin or UserRole.Manager or UserRole.Cashier))
        {
            throw new ForbiddenAppException(ErrorCodes.Forbidden, "This role cannot void sales.");
        }

        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        // One nowUtc for this entire attempt — reused for both the same-day eligibility check and
        // the VoidedAtUtc stamp, so the two can never disagree even under a slow request.
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        var sale = await _db.Sales.Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        // Branch-scoped users get 404 (not 403) on a foreign sale, matching ReturnService.
        var assignedBranch = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assignedBranch is { } branchId && branchId != sale.BranchId)
        {
            throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }

        // First-pass eligibility check — fast-fails the common case before resolving approver
        // credentials or opening a transaction. NOT authoritative by itself; re-checked below.
        await EnsureEligibleAsync(sale, tenantId, nowUtc, cancellationToken);

        var (voidedByUserId, approvedByUserId) = await ResolveActorAsync(sale.BranchId, request, cancellationToken);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Pessimistic lock on the session row — the FIRST statement inside the transaction, before
        // any other read. RowVersion (Sale) cannot protect this cross-entity race: Void never writes
        // to RegisterSessions, so there's no natural optimistic-concurrency collision to detect
        // against a concurrent close. This lock instead fully serializes the two operations: whoever
        // acquires it first runs their entire read-then-write to completion before the other can even
        // begin reading — but only once RegisterSessionService.ReconcileAndCloseAsync also takes the
        // matching lock, which it does not yet at this commit (Task B5 adds it). Until then, this lock
        // alone does not close the void-vs-close race; see the plan's Global Constraints.
        await _db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {sale.RegisterSessionId}")
            .ToListAsync(cancellationToken);

        // Authoritative re-check, now that the session row is locked for the rest of this transaction.
        await EnsureEligibleAsync(sale, tenantId, nowUtc, cancellationToken);

        var lineVariantIds = sale.Items.Select(i => i.ProductVariantId).ToList();
        var trackedVariantIds = (await _db.ProductVariants.AsNoTracking()
            .Where(v => v.TenantId == tenantId && lineVariantIds.Contains(v.Id))
            .Join(_db.Products.Where(p => p.TrackInventory), v => v.ProductId, p => p.Id, (v, _) => v.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var item in sale.Items.Where(i => trackedVariantIds.Contains(i.ProductVariantId)))
        {
            await _inventory.ReverseForVoidAsync(
                tenantId, sale.BranchId, item.ProductVariantId, item.Quantity, sale.Id, _currentUser.UserId, cancellationToken);
        }

        sale.Void(voidedByUserId, request.Reason, approvedByUserId, nowUtc);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Sale.RowVersion caught a race this lock doesn't cover — another void, or a Return,
            // committed between our load and our write. Roll back (the inventory reversal above
            // rolls back with it — it's the same DB transaction, so it's never left half-applied),
            // then report the sale's actual current state instead of a generic conflict.
            await transaction.RollbackAsync(cancellationToken);
            await EnsureEligibleAsync(null, tenantId, nowUtc, cancellationToken, saleId);
            throw new InvalidOperationException("Unreachable — EnsureEligibleAsync always throws when re-evaluating a lost race.");
        }

        await transaction.CommitAsync(cancellationToken);

        return await _saleQuery.GetAsync(saleId, cancellationToken);
    }

    /// <summary>
    /// Re-derives and evaluates eligibility from a fresh read. Pass the already-loaded
    /// <paramref name="sale"/> for the pre-lock fast-fail and the post-lock authoritative check
    /// (same tracked instance, so `sale.Items` stays populated); pass <c>null</c> with
    /// <paramref name="saleId"/> instead when re-evaluating after a lost `DbUpdateConcurrencyException`
    /// race, since the tracked instance's in-memory state is no longer trustworthy at that point.
    /// Always throws — never returns normally — since every call site only calls this when it
    /// already needs the (possibly now-different) typed error.
    /// </summary>
    private async Task EnsureEligibleAsync(
        Sale? sale, Guid tenantId, DateTime nowUtc, CancellationToken cancellationToken, Guid? saleId = null)
    {
        var current = sale ?? await _db.Sales.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.Id == saleId!.Value, cancellationToken);

        var hasReturns = await _db.SaleReturns.AnyAsync(r => r.TenantId == tenantId && r.SaleId == current.Id, cancellationToken);
        var session = await _db.RegisterSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.Id == current.RegisterSessionId, cancellationToken);
        var sessionOpen = session.Status == RegisterSessionStatus.Open;
        var sameUtcDay = current.CompletedAtUtc is { } completedAt && completedAt.Date == nowUtc.Date;

        var eligibility = VoidEligibility.Evaluate(current, sessionOpen, sameUtcDay, hasReturns);
        if (!eligibility.CanVoid)
        {
            throw new BusinessRuleException(eligibility.IneligibilityCode!, IneligibilityMessage(eligibility.IneligibilityCode!));
        }
        // eligibility.CanVoid == true here should only happen on the two authoritative pre-write
        // calls (which then proceed to write); if called after a lost race, CanVoid should always
        // be false (the very state change that beat us is what makes it ineligible) — if it somehow
        // isn't, that's a bug worth a loud failure rather than a silent no-op, hence no return path.
    }

    private async Task<(Guid VoidedBy, Guid? ApprovedBy)> ResolveActorAsync(
        Guid saleBranchId, VoidSaleRequest request, CancellationToken cancellationToken)
    {
        var role = _currentUser.Role;

        if (role is UserRole.Owner or UserRole.Admin or UserRole.Manager)
        {
            // Manager's branch match was already asserted by the 404 guard above (AssignedBranchIdAsync).
            return (_currentUser.UserId, null);
        }

        // role == UserRole.Cashier, explicitly — the top-of-method guard already rejected every
        // other role, so this is never reached as a fallback for "anything else."
        if (await _permissions.HasGrantAsync(_currentUser.UserId, cancellationToken))
        {
            return (_currentUser.UserId, null);
        }

        if (request.Approval is null)
        {
            throw new BusinessRuleException(ErrorCodes.VoidApprovalRequired,
                "You don't have permission to void completed sales. An authorized Manager, Admin, or Owner must approve this void.");
        }

        var approverId = await _approverVerification.VerifyAsync(
            request.Approval.ApproverEmail, request.Approval.ApproverPassword, saleBranchId, cancellationToken);
        return (_currentUser.UserId, approverId);
    }

    private static string IneligibilityMessage(string code) => code switch
    {
        ErrorCodes.SaleNotVoidable => "This sale cannot be voided.",
        ErrorCodes.SaleHasReturns => "This sale has returns against it and cannot be voided.",
        ErrorCodes.VoidSessionClosed => "This sale can no longer be voided because its register session has already been closed. Use the return/refund process instead.",
        ErrorCodes.VoidCutoffExpired => "This sale is past the void cutoff. Use the return/refund process instead.",
        _ => "This sale cannot be voided."
    };
}
