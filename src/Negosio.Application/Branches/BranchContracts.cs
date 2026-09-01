namespace Negosio.Application.Branches;

/// <summary>Branch shape for selectors and management screens.</summary>
public sealed record BranchDto(
    Guid Id,
    string Name,
    string Code,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string Province,
    string? PostalCode,
    bool IsActive,
    DateTime CreatedAtUtc,
    int AssignedStaffCount);

public sealed record CreateBranchRequest(
    string Name,
    string Code,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string Province,
    string? PostalCode);

public sealed record UpdateBranchRequest(
    string Name,
    string AddressLine1,
    string? AddressLine2,
    string City,
    string Province,
    string? PostalCode);

public interface IBranchQueryService
{
    /// <summary>
    /// Branch selector feed. Owner/Admin: active branches, or active + inactive when
    /// <paramref name="includeInactive"/> is set. Branch-scoped users: always exactly their
    /// assigned branch.
    /// </summary>
    Task<IReadOnlyList<BranchDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
}

public interface IBranchManagementService
{
    Task<BranchDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BranchDto> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken = default);

    Task<BranchDto> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken = default);

    Task<BranchDto> DeactivateAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BranchDto> ReactivateAsync(Guid id, CancellationToken cancellationToken = default);
}
