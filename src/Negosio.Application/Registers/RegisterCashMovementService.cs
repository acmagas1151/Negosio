using Microsoft.EntityFrameworkCore;
using FluentValidation;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed class RegisterCashMovementService : IRegisterCashMovementService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IValidator<CreateCashMovementRequest> _validator;

    public RegisterCashMovementService(
        ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess,
        IValidator<CreateCashMovementRequest> validator)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _validator = validator;
    }

    public async Task<RegisterCashMovementDto> CreateAsync(
        Guid sessionId, CreateCashMovementRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var session = await _db.RegisterSessions
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        if (session.OpenedByUserId != _currentUser.UserId)
        {
            throw new ForbiddenAppException(ErrorCodes.CashMovementNotOwner, "This register session belongs to another user.");
        }

        if (session.Status != RegisterSessionStatus.Open)
        {
            throw new BusinessRuleException(ErrorCodes.CashMovementSessionClosed, "This register session is not open.");
        }

        var movement = RegisterCashMovement.Create(
            tenantId, session.BranchId, session.Id, request.Type, request.Amount, request.Reason, _currentUser.UserId);
        _db.RegisterCashMovements.Add(movement);
        await _db.SaveChangesAsync(cancellationToken);

        return await ToDtoAsync(movement, cancellationToken);
    }

    public async Task<IReadOnlyList<RegisterCashMovementDto>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var tenantId = _currentUser.TenantId;

        var session = await _db.RegisterSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == sessionId, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");

        // Owner/Admin: any session. Manager: their own branch. Cashier: only their own session.
        if (!BranchRoles.IsAllBranch(_currentUser.Role))
        {
            var assigned = (await _branchAccess.AssignedBranchIdAsync(cancellationToken))!.Value;
            if (session.BranchId != assigned)
            {
                throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
            }

            if (_currentUser.Role == UserRole.Cashier && session.OpenedByUserId != _currentUser.UserId)
            {
                throw new NotFoundException(ErrorCodes.RegisterSessionNotFound, "Register session not found.");
            }
        }

        var rows = await _db.RegisterCashMovements.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.RegisterSessionId == sessionId)
            .OrderBy(m => m.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var names = await _db.Users.AsNoTracking()
            .Where(u => rows.Select(r => r.CreatedByUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}".Trim(), cancellationToken);

        return rows.Select(m => new RegisterCashMovementDto(
            m.Id, m.Type, m.Amount, m.Reason, m.CreatedByUserId,
            names.GetValueOrDefault(m.CreatedByUserId, string.Empty), m.CreatedAtUtc)).ToList();
    }

    private async Task<RegisterCashMovementDto> ToDtoAsync(RegisterCashMovement m, CancellationToken cancellationToken)
    {
        var name = await _db.Users.AsNoTracking().Where(u => u.Id == m.CreatedByUserId)
            .Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        return new RegisterCashMovementDto(m.Id, m.Type, m.Amount, m.Reason, m.CreatedByUserId, name, m.CreatedAtUtc);
    }
}
