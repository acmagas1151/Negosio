namespace Negosio.Application.Branches;

/// <summary>Minimal branch shape for UI selectors (inventory, POS, reports). Read-only.</summary>
public sealed record BranchDto(Guid Id, string Name, string Code, bool IsActive);

public interface IBranchQueryService
{
    /// <summary>Branches for the current tenant. Active only unless <paramref name="includeInactive"/> is set.</summary>
    Task<IReadOnlyList<BranchDto>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default);
}
