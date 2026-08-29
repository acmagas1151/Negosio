using Microsoft.EntityFrameworkCore;

namespace Negosio.Application.Common;

/// <summary>
/// The single pagination envelope returned by every list endpoint. Page sizes are always bounded
/// (see <see cref="MaxPageSize"/>) so a standard list API can never return an unbounded result set.
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    /// <summary>Clamp a client-supplied (page, pageSize) into the allowed range.</summary>
    public static (int Page, int PageSize) Normalize(int page, int pageSize)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };
        return (safePage, safeSize);
    }

    /// <summary>Run the count + page slice server-side and build the envelope.</summary>
    public static async Task<PagedResult<T>> CreateAsync(
        IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (safePage, safeSize) = Normalize(page, pageSize);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(cancellationToken);

        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)safeSize);

        return new PagedResult<T>(items, safePage, safeSize, totalCount, totalPages);
    }
}
