using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;

namespace Negosio.Application.Catalog;

public sealed class CategoryService : ICategoryService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateCategoryRequest> _createValidator;
    private readonly IValidator<UpdateCategoryRequest> _updateValidator;

    public CategoryService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateCategoryRequest> createValidator,
        IValidator<UpdateCategoryRequest> updateValidator)
    {
        _db = db;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<PagedResult<CategoryDto>> ListAsync(CategoryListQuery query, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var categories = _db.Categories.AsNoTracking().Where(c => c.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            categories = categories.Where(c => c.Name.Contains(term));
        }

        if (query.IsActive is { } isActive)
        {
            categories = categories.Where(c => c.IsActive == isActive);
        }

        var projected = categories
            .OrderBy(c => c.Name)
            .Select(c => new CategoryDto(
                c.Id,
                c.Name,
                c.Description,
                c.IsActive,
                _db.Products.Count(p => p.TenantId == tenantId && p.CategoryId == c.Id),
                c.CreatedAtUtc,
                c.UpdatedAtUtc));

        return await PagedResult<CategoryDto>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
    }

    public async Task<CategoryDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var dto = await _db.Categories.AsNoTracking()
            .Where(c => c.TenantId == tenantId && c.Id == id)
            .Select(c => new CategoryDto(
                c.Id, c.Name, c.Description, c.IsActive,
                _db.Products.Count(p => p.TenantId == tenantId && p.CategoryId == c.Id),
                c.CreatedAtUtc, c.UpdatedAtUtc))
            .SingleOrDefaultAsync(cancellationToken);

        return dto ?? throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");
    }

    public async Task<CategoryDto> CreateAsync(CreateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _createValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var normalized = Category.Normalize(request.Name.Trim());
        if (await _db.Categories.AnyAsync(c => c.TenantId == tenantId && c.NormalizedName == normalized, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.CategoryAlreadyExists, "A category with this name already exists.");
        }

        var category = Category.Create(tenantId, request.Name, request.Description);
        _db.Categories.Add(category);

        await SaveOrTranslateAsync(cancellationToken);

        return await GetAsync(category.Id, cancellationToken);
    }

    public async Task<CategoryDto> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _updateValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var category = await _db.Categories
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");

        var normalized = Category.Normalize(request.Name.Trim());
        if (await _db.Categories.AnyAsync(
                c => c.TenantId == tenantId && c.Id != id && c.NormalizedName == normalized, cancellationToken))
        {
            throw new ConflictException(ErrorCodes.CategoryAlreadyExists, "A category with this name already exists.");
        }

        category.UpdateDetails(request.Name, request.Description);
        if (request.IsActive)
        {
            category.Activate();
        }
        else
        {
            category.Deactivate();
        }

        await SaveOrTranslateAsync(cancellationToken);

        return await GetAsync(category.Id, cancellationToken);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();

        var category = await _db.Categories
            .SingleOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, cancellationToken)
            ?? throw new NotFoundException(ErrorCodes.CategoryNotFound, "Category not found.");

        category.Deactivate();
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
            throw new ConflictException(ErrorCodes.CategoryAlreadyExists, "A category with this name already exists.");
        }
    }
}
