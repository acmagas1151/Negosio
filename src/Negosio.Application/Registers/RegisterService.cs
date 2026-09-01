using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Registers;

public sealed class RegisterService : IRegisterService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateRegisterRequest> _createValidator;
    private readonly IValidator<UpdateRegisterRequest> _updateValidator;
    private readonly IBranchAccessResolver _branchAccess;

    public RegisterService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateRegisterRequest> createValidator,
        IValidator<UpdateRegisterRequest> updateValidator,
        IBranchAccessResolver branchAccess)
    {
        _db = db;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _branchAccess = branchAccess;
    }

    public async Task<PagedResult<RegisterDto>> ListAsync(RegisterListQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        query = query with { BranchId = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken) };

        var registers = _db.Registers.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (query.BranchId is { } branchId)
        {
            registers = registers.Where(r => r.BranchId == branchId);
        }

        if (query.IsActive is { } isActive)
        {
            registers = registers.Where(r => r.IsActive == isActive);
        }

        var projected = registers
            .OrderBy(r => r.Name)
            .Select(r => new RegisterDto(
                r.Id, r.BranchId,
                _db.Branches.Where(b => b.Id == r.BranchId).Select(b => b.Name).FirstOrDefault() ?? string.Empty,
                r.Name, r.Code, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc));

        return await PagedResult<RegisterDto>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
    }

    public async Task<RegisterDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var dto = await _db.Registers.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == id)
            .Select(r => new RegisterDto(
                r.Id, r.BranchId,
                _db.Branches.Where(b => b.Id == r.BranchId).Select(b => b.Name).FirstOrDefault() ?? string.Empty,
                r.Name, r.Code, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return dto ?? throw new NotFoundException(ErrorCodes.RegisterNotFound, "Register not found.");
    }

    public async Task<RegisterDto> CreateAsync(CreateRegisterRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _createValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var branchId = await _branchAccess.ResolveTargetBranchAsync(request.BranchId, cancellationToken: cancellationToken);

        var register = Register.Create(tenantId, branchId, request.Name, request.Code);
        _db.Registers.Add(register);

        await SaveOrTranslateAsync(cancellationToken);

        return await GetAsync(register.Id, cancellationToken);
    }

    public async Task<RegisterDto> UpdateAsync(Guid id, UpdateRegisterRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _updateValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var register = await _db.Registers
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterNotFound, "Register not found.");

        register.UpdateDetails(request.Name, request.Code);
        if (request.IsActive)
        {
            register.Activate();
        }
        else
        {
            register.Deactivate();
        }

        await SaveOrTranslateAsync(cancellationToken);

        return await GetAsync(register.Id, cancellationToken);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var register = await _db.Registers
            .SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.RegisterNotFound, "Register not found.");

        register.Deactivate();
        await _db.SaveChangesAsync(cancellationToken);
    }

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }

    private async Task SaveOrTranslateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            throw new ConflictException(ErrorCodes.RegisterAlreadyExists, "A register with this code already exists in this branch.");
        }
    }
}
