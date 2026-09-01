using Negosio.Application.Common;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed record RegisterDto(
    Guid Id,
    Guid BranchId,
    string BranchName,
    string Name,
    string Code,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record CreateRegisterRequest(Guid BranchId, string Name, string Code);

public sealed record UpdateRegisterRequest(string Name, string Code, bool IsActive);

public sealed record RegisterListQuery(
    Guid? BranchId = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = PagedResult<RegisterDto>.DefaultPageSize);

public sealed record RegisterSessionDto(
    Guid Id,
    Guid BranchId,
    Guid RegisterId,
    string RegisterName,
    RegisterSessionStatus Status,
    Guid OpenedByUserId,
    string OpenedByName,
    DateTime OpenedAtUtc,
    DateTime? ClosedAtUtc,
    decimal OpeningCash,
    decimal? ClosingCash,
    decimal? ExpectedCash,
    decimal? CashDifference);

public sealed record OpenRegisterSessionRequest(Guid RegisterId, decimal OpeningCash);

public sealed record CloseRegisterSessionRequest(decimal ClosingCash);

public interface IRegisterService
{
    Task<PagedResult<RegisterDto>> ListAsync(RegisterListQuery query, CancellationToken cancellationToken = default);

    Task<RegisterDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<RegisterDto> CreateAsync(CreateRegisterRequest request, CancellationToken cancellationToken = default);

    Task<RegisterDto> UpdateAsync(Guid id, UpdateRegisterRequest request, CancellationToken cancellationToken = default);

    Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IRegisterSessionService
{
    Task<RegisterSessionDto> OpenAsync(OpenRegisterSessionRequest request, CancellationToken cancellationToken = default);

    Task<RegisterSessionDto> CloseAsync(Guid sessionId, CloseRegisterSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Owner/Admin only — close a session owned by someone else (same reconciliation).</summary>
    Task<RegisterSessionDto> ForceCloseAsync(Guid sessionId, CloseRegisterSessionRequest request, CancellationToken cancellationToken = default);

    Task<RegisterSessionDto> GetCurrentAsync(Guid? registerId, Guid? branchId, CancellationToken cancellationToken = default);
}
