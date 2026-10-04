using Negosio.Application.Common;
using Negosio.Application.Sales;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Registers;

public sealed record RegisterOpenSessionDto(
    Guid SessionId,
    Guid OpenedByUserId,
    string OpenedByName,
    DateTime OpenedAtUtc,
    decimal OpeningCash);

public sealed record RegisterDto(
    Guid Id,
    Guid BranchId,
    string BranchName,
    string Name,
    string Code,
    bool IsActive,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    RegisterOpenSessionDto? OpenSession = null);

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
    decimal? CashDifference,
    decimal? GrossCashSales,
    decimal? VoidedCashSales,
    decimal? RefundCashOut,
    decimal? CashIn,
    decimal? CashOut);

public sealed record OpenRegisterSessionRequest(Guid RegisterId, decimal OpeningCash);

public sealed record CloseRegisterSessionRequest(decimal ClosingCash);

/// <summary>A live, advisory snapshot of what a still-OPEN session's expected cash would be if closed
/// right now — lets the close-session UI show "over/short" in its confirmation step, before the
/// counted cash is actually submitted. Not authoritative: the real close recomputes this itself,
/// under its own pessimistic lock, so a sale/void/cash-movement landing between this preview and the
/// actual close is reflected correctly in the final result regardless of what this showed.</summary>
public sealed record ExpectedCashPreviewDto(
    decimal OpeningCash, CashReconciliationBreakdown Breakdown, decimal ExpectedCash);

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

    /// <summary>Live, advisory expected-cash snapshot for a still-open session — see
    /// <see cref="ExpectedCashPreviewDto"/>. Same scope as <see cref="CloseAsync"/>/<see cref="ForceCloseAsync"/>:
    /// the session's own opener, or Owner/Admin.</summary>
    Task<ExpectedCashPreviewDto> PreviewExpectedCashAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public sealed record RegisterCashMovementDto(
    Guid Id, CashMovementType Type, decimal Amount, string Reason,
    Guid CreatedByUserId, string CreatedByName, DateTime CreatedAtUtc,
    /// <summary>The Manager/Admin/Owner who approved a Cashier's cash movement — null when the creator
    /// acted directly (a privileged role, or a Cashier with the grant).</summary>
    string? ApprovedByName = null);

public sealed record CreateCashMovementRequest(
    CashMovementType Type, decimal Amount, string Reason,
    /// <summary>Supplied only on a retry after the server returns CASH_MOVEMENT_APPROVAL_REQUIRED.</summary>
    VoidSaleApprovalInput? Approval = null);

public interface IRegisterCashMovementService
{
    Task<RegisterCashMovementDto> CreateAsync(Guid sessionId, CreateCashMovementRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegisterCashMovementDto>> ListAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
