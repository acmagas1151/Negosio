using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed class RegisterSessionService : IRegisterSessionService
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<OpenRegisterSessionRequest> _openValidator;
    private readonly IValidator<CloseRegisterSessionRequest> _closeValidator;

    public RegisterSessionService(
        IApplicationDbContext db,
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
            throw new ConflictException(ErrorCodes.RegisterSessionAlreadyOpen, "This register already has an open session.");
        }

        var session = RegisterSession.Open(tenantId, register.BranchId, register.Id, _currentUser.UserId, request.OpeningCash);
        _db.RegisterSessions.Add(session);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            // Lost the race against the filtered unique open-session index.
            throw new ConflictException(ErrorCodes.RegisterSessionAlreadyOpen, "This register already has an open session.");
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

        if (session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.RegisterSessionNotOpen, "This register session is not open.");
        }

        var saleIds = _db.Sales.Where(s => s.TenantId == tenantId && s.RegisterSessionId == sessionId).Select(s => s.Id);

        var cashIn = await _db.Payments
            .Where(p => p.TenantId == tenantId && p.Method == PaymentMethod.Cash && saleIds.Contains(p.SaleId))
            .SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;

        var returnIds = _db.SaleReturns.Where(r => r.TenantId == tenantId && saleIds.Contains(r.SaleId)).Select(r => r.Id);
        var cashOut = await _db.RefundPayments
            .Where(r => r.TenantId == tenantId && r.Method == PaymentMethod.Cash && returnIds.Contains(r.SaleReturnId))
            .SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;

        var expected = session.OpeningCash + cashIn - cashOut;
        session.Close(_currentUser.UserId, request.ClosingCash, expected);
        await _db.SaveChangesAsync(cancellationToken);

        return await ProjectAsync(session.Id, tenantId, cancellationToken);
    }

    public async Task<RegisterSessionDto> GetCurrentAsync(Guid? registerId, Guid? branchId, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var open = _db.RegisterSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.Status == RegisterSessionStatus.Open);

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
                s.OpeningCash, s.ClosingCash, s.ExpectedCash, s.CashDifference))
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
