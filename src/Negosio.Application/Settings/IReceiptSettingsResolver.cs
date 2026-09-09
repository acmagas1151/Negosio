using Negosio.Domain.Common;

namespace Negosio.Application.Settings;

/// <summary>
/// Read-only resolver for the effective <see cref="ReceiptSettingsValues"/> of a branch, used by the
/// receipt render path (Task 8). Deliberately a small duplication of
/// <c>ReceiptSettingsService</c>'s private effective-settings lookup: the service needs a tracked row
/// for mutation, this needs read-only values. Scoped, memoized per request.
/// </summary>
public interface IReceiptSettingsResolver
{
    Task<ReceiptSettingsValues> ResolveAsync(Guid branchId, CancellationToken ct = default);
}
