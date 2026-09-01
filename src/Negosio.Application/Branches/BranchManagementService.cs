using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Branches;

public sealed class BranchManagementService : IBranchManagementService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateBranchRequest> _createValidator;
    private readonly IValidator<UpdateBranchRequest> _updateValidator;

    public BranchManagementService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateBranchRequest> createValidator,
        IValidator<UpdateBranchRequest> updateValidator)
    {
        _db = db;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<BranchDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var dto = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && b.Id == id)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

        return dto ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");
    }

    public async Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _createValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var branch = Branch.Create(
            tenantId, request.Name, request.Code, request.AddressLine1,
            request.AddressLine2, request.City, request.Province, request.PostalCode);
        _db.Branches.Add(branch);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            throw new ConflictException(ErrorCodes.DuplicateBranchCode, "A branch with this code already exists.");
        }

        return await GetAsync(branch.Id, cancellationToken);
    }

    public async Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _updateValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var branch = await _db.Branches
            .SingleOrDefaultAsync(b => b.TenantId == tenantId && b.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");

        branch.UpdateDetails(
            request.Name, request.AddressLine1, request.AddressLine2,
            request.City, request.Province, request.PostalCode);

        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<BranchDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var branch = await _db.Branches
            .SingleOrDefaultAsync(b => b.TenantId == tenantId && b.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");

        if (branch.IsActive)
        {
            var otherActive = await _db.Branches
                .CountAsync(b => b.TenantId == tenantId && b.IsActive && b.Id != id, cancellationToken);
            if (otherActive == 0)
            {
                throw new BusinessRuleException(
                    ErrorCodes.LastActiveBranch, "You can't deactivate the last active branch.");
            }

            branch.Deactivate();
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<BranchDto> ReactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var branch = await _db.Branches
            .SingleOrDefaultAsync(b => b.TenantId == tenantId && b.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.BranchNotFound, "Branch not found.");

        if (!branch.IsActive)
        {
            branch.Reactivate();
            await _db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(id, cancellationToken);
    }

    private System.Linq.Expressions.Expression<Func<Branch, BranchDto>> Projection =>
        b => new BranchDto(
            b.Id, b.Name, b.Code, b.AddressLine1, b.AddressLine2, b.City, b.Province, b.PostalCode,
            b.IsActive, b.CreatedAtUtc,
            _db.Users.Count(u => u.BranchId == b.Id && u.IsActive));

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
