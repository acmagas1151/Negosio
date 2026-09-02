using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed class RegisterSessionService : IRegisterSessionService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<OpenRegisterSessionRequest> _openValidator;
    private readonly IValidator<CloseRegisterSessionRequest> _closeValidator;

    public RegisterSessionService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<OpenRegisterSessionRequest> openValidator,
        IValidator<CloseRegisterSessionRequest> closeValidator)
    {
        _db = db;
        _currentUser = currentUser;
        _openValidator = openValidator;
        _closeValidator = closeValidator;
    }

    public async Task<RegisterSessionDto> OpenAsync(OpenRegisterSessionRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _openValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var register = await _db.Registers
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == request.RegisterId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterNotFound, "Register not found.");

        if (!register.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.RegisterNotFound, "This register is inactive.");
        }

        var alreadyOpen = await _db.RegisterSessions
            .AnyAsync(s => s.TenantId == tenantId && s.RegisterId == register.Id && s.Status == RegisterSessionStatus.Open, cancellationToken);
        if (alreadyOpen)
        {
            throw new ConflictException(ErrorCodes.RegisterSessionAlreadyOpen, "This register is already in use.");
        }

        var mineOpen = await _db.RegisterSessions
            .AnyAsync(s => s.TenantId == tenantId && s.OpenedByUserId == _currentUser.UserId && s.Status == RegisterSessionStatus.Open, cancellationToken);
        if (mineOpen)
        {
            throw new ConflictException(ErrorCodes.CashierSessionOpen,
                "You already have an open register session. Continue or close it first.");
        }

        var session = RegisterSession.Open(tenantId, register.BranchId, register.Id, _currentUser.UserId, request.OpeningCash);
        _db.RegisterSessions.Add(session);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name))
        {
            // Lost the race against a filtered unique open-session index.
            if (name?.Contains("OpenedByUserId", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new ConflictException(ErrorCodes.CashierSessionOpen,
                    "You already have an open register session. Continue or close it first.");
            }

            throw new ConflictException(ErrorCodes.RegisterSessionAlreadyOpen, "This register is already in use.");
        }

        return await ProjectAsync(session.Id, tenantId, cancellationToken);
    }

    public async Task<RegisterSessionDto> CloseAsync(Guid sessionId, CloseRegisterSessionRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _closeValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var session = await _db.RegisterSessions
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        if (session.OpenedByUserId != _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.SessionNotOwned, "This register session belongs to another user.");
        }

        return await ReconcileAndCloseAsync(session, tenantId, request.ClosingCash, cancellationToken);
    }

    /// <summary>Owner/Admin override: close another user's stuck session with the same reconciliation.</summary>
    public async Task<RegisterSessionDto> ForceCloseAsync(
        Guid sessionId, CloseRegisterSessionRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _closeValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var session = await _db.RegisterSessions
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        return await ReconcileAndCloseAsync(session, tenantId, request.ClosingCash, cancellationToken);
    }

    private async Task<RegisterSessionDto> ReconcileAndCloseAsync(
        RegisterSession session, Guid tenantId, decimal closingCash, CancellationToken cancellationToken)
    {
        if (session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RegisterSessionNotOpen, "This register session is not open.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Same pessimistic lock VoidSaleService.VoidAsync takes, taken here as the very first read too
        // — not just before the final write. Whoever gets here first (this close, or a concurrent void)
        // runs its entire read-then-write sequence to completion (commit or rollback, which releases the
        // lock) before the other can even begin reading, so these SUM queries below can never be a stale
        // snapshot relative to a void that commits moments later, or vice versa. HOLDLOCK's guarantee
        // depends entirely on this running inside the explicit transaction above.
        await _db.Database.SqlQuery<int>(
            $"SELECT 1 AS Value FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = {session.Id}")
            .ToListAsync(cancellationToken);

        var saleIds = _db.Sales.Where(s => s.TenantId == tenantId && s.RegisterSessionId == session.Id).Select(s => s.Id);

        // Gross: every cash payment on this session's sales, regardless of a later void — Payment rows
        // are never deleted. Voided: the subset of that belonging to sales now Status == Voided, so the
        // UI can show "Gross" and "Voided" as two distinct lines rather than a pre-subtracted number.
        var grossCashSales = await _db.Payments
            .Where(p => p.TenantId == tenantId && p.Method == PaymentMethod.Cash && saleIds.Contains(p.SaleId))
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var voidedSaleIds = _db.Sales.Where(s => s.TenantId == tenantId && s.RegisterSessionId == session.Id && s.Status == SaleStatus.Voided).Select(s => s.Id);
        var voidedCashSales = await _db.Payments
            .Where(p => p.TenantId == tenantId && p.Method == PaymentMethod.Cash && voidedSaleIds.Contains(p.SaleId))
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var returnIds = _db.SaleReturns.Where(r => r.TenantId == tenantId && saleIds.Contains(r.SaleId)).Select(r => r.Id);
        var refundCashOut = await _db.RefundPayments
            .Where(r => r.TenantId == tenantId && r.Method == PaymentMethod.Cash && returnIds.Contains(r.SaleReturnId))
            .SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;

        var cashIn = await _db.RegisterCashMovements
            .Where(m => m.TenantId == tenantId && m.RegisterSessionId == session.Id && m.Type == CashMovementType.CashIn)
            .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;
        var cashOut = await _db.RegisterCashMovements
            .Where(m => m.TenantId == tenantId && m.RegisterSessionId == session.Id && m.Type == CashMovementType.CashOut)
            .SumAsync(m => (decimal?)m.Amount, cancellationToken) ?? 0m;

        var expected = session.OpeningCash + grossCashSales - voidedCashSales - refundCashOut + cashIn - cashOut;
        var breakdown = new CashReconciliationBreakdown(grossCashSales, voidedCashSales, refundCashOut, cashIn, cashOut);

        // ClosedByUserId = the acting user (the original cashier on a normal close, an Owner/Admin
        // on a force-close); OpenedByUserId is never touched.
        session.Close(_currentUser.UserId, closingCash, expected, breakdown);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await ProjectAsync(session.Id, tenantId, cancellationToken);
    }

    public async Task<RegisterSessionDto> GetCurrentAsync(Guid? registerId, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        // "The current session" for the POS flow means the caller's own open session.
        var open = _db.RegisterSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId
                && s.Status == RegisterSessionStatus.Open
                && s.OpenedByUserId == _currentUser.UserId);

        if (registerId is { } rid)
        {
            open = open.Where(s => s.RegisterId == rid);
        }
        else if (branchId is { } bid)
        {
            open = open.Where(s => s.BranchId == bid);
        }
        else
        {
            // No hint: only works when the tenant has exactly one branch.
            var branchIds = await _db.Branches.Where(b => b.TenantId == tenantId).Select(b => b.Id).Take(2).ToListAsync(cancellationToken);
            if (branchIds.Count == 1)
            {
                open = open.Where(s => s.BranchId == branchIds[0]);
            }
            else
            {
                throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Specify a register or branch to find the open session.");
            }
        }

        var id = await open.OrderByDescending(s => s.OpenedAtUtc).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(cancellationToken);
        if (id is null)
        {
            throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "No open register session.");
        }

        return await ProjectAsync(id.Value, tenantId, cancellationToken);
    }

    private async Task<RegisterSessionDto> ProjectAsync(Guid sessionId, Guid tenantId, CancellationToken cancellationToken)
    {
        var dto = await (
            from s in _db.RegisterSessions.AsNoTracking().Where(s => s.TenantId == tenantId && s.Id == sessionId)
            join r in _db.Registers on s.RegisterId equals r.Id
            select new RegisterSessionDto(
                s.Id, s.BranchId, s.RegisterId, r.Name, s.Status,
                s.OpenedByUserId,
                _db.Users.Where(u => u.Id == s.OpenedByUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefault() ?? string.Empty,
                s.OpenedAtUtc, s.ClosedAtUtc,
                s.OpeningCash, s.ClosingCash, s.ExpectedCash, s.CashDifference,
                s.GrossCashSales, s.VoidedCashSales, s.RefundCashOut, s.CashIn, s.CashOut))
            .SingleOrDefaultAsync(cancellationToken);

        return dto ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
    }

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
