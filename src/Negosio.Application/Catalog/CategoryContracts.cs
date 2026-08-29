using Negosio.Application.Common;

namespace Negosio.Application.Catalog;

public sealed record CategoryDto(
    Guid Id,
    string Name,
    string? Description,
    bool IsActive,
    int ProductCount,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CreateCategoryRequest(string Name, string? Description);

public sealed record UpdateCategoryRequest(string Name, string? Description, bool IsActive);

public sealed record CategoryListQuery(
    string? Search = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = PagedResult<CategoryDto>.DefaultPageSize);

public interface ICategoryService
{
    Task<PagedResult<CategoryDto>> ListAsync(CategoryListQuery query, CancellationToken cancellationToken = default);

    Task<CategoryDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<CategoryDto> CreateAsync(CreateCategoryRequest request, CancellationToken cancellationToken = default);

    Task<CategoryDto> UpdateAsync(Guid id, UpdateCategoryRequest request, CancellationToken cancellationToken = default);

    Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
