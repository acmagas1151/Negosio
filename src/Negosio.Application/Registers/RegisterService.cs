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
                r.Name, r.Code, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc, null));

        var page = await PagedResult<RegisterDto>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var withSessions = await AttachOpenSessionsAsync(tenantId, page.Items, cancellationToken);
        return new PagedResult<RegisterDto>(withSessions, page.Page, page.PageSize, page.TotalCount, page.TotalPages);
    }

    public async Task<RegisterDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var dto = await _db.Registers.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Id == id)
            .Select(r => new RegisterDto(
                r.Id, r.BranchId,
                _db.Branches.Where(b => b.Id == r.BranchId).Select(b => b.Name).FirstOrDefault() ?? string.Empty,
                r.Name, r.Code, r.IsActive, r.CreatedAtUtc, r.UpdatedAtUtc, null))
            .SingleOrDefaultAsync(cancellationToken);

        if (dto is null)
        {
            throw new NotFoundException(ErrorCodes.RegisterNotFound, "Register not found.");
        }

        return (await AttachOpenSessionsAsync(tenantId, new[] { dto }, cancellationToken))[0];
    }

    /// <summary>Stitch each register's current open session (any owner) into the DTO for the management view.</summary>
    private async Task<IReadOnlyList<RegisterDto>> AttachOpenSessionsAsync(
        Guid tenantId, IReadOnlyList<RegisterDto> registers, CancellationToken cancellationToken)
    {
        var ids = registers.Select(r => r.Id).ToList();
        if (ids.Count == 0)
        {
            return registers;
        }

        var sessions = await (
            from s in _db.RegisterSessions.AsNoTracking()
                .Where(s => s.TenantId == tenantId
                    && s.Status == Domain.Enums.RegisterSessionStatus.Open
                    && ids.Contains(s.RegisterId))
            join u in _db.Users on s.OpenedByUserId equals u.Id
            select new
            {
                s.RegisterId,
                Dto = new RegisterOpenSessionDto(s.Id, s.OpenedByUserId, u.FirstName + " " + u.LastName, s.OpenedAtUtc, s.OpeningCash),
            })
            .ToDictionaryAsync(x => x.RegisterId, x => x.Dto, cancellationToken);

        return registers
            .Select(r => sessions.TryGetValue(r.Id, out var open) ? r with { OpenSession = open } : r)
            .ToList();
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
