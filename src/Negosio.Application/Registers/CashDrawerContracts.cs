using Negosio.Application.Sales;

namespace Negosio.Application.Registers;

/// <summary>Reuses the same approval shape Void uses (approver email + password) — one input, one meaning.</summary>
public sealed record OpenCashDrawerRequest(VoidSaleApprovalInput? Approval = null);

public sealed record CashDrawerOpenDto(
    Guid Id,
    Guid RegisterSessionId,
    Guid RequestedByUserId,
    string RequestedByName,
    Guid? ApprovedByUserId,
    string? ApprovedByName,
    DateTime CreatedAtUtc);

public interface ICashDrawerService
{
    Task<CashDrawerOpenDto> OpenAsync(Guid sessionId, OpenCashDrawerRequest request, CancellationToken cancellationToken = default);
}
