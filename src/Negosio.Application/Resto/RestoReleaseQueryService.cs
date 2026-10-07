using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;

namespace Negosio.Application.Resto;

public interface IRestoReleaseQueryService
{
    /// <summary>Settled Pay-as-you-order rounds still waiting for release. Oldest first; <paramref name="limit"/> is capped at 200.</summary>
    Task<RestoKeysetPageDto<PendingPayoReleaseRowDto>> ListPendingReleasesAsync(
        Guid? branchId, Guid? afterOrderId, int? limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Kitchen tickets available to the kitchen but unacknowledged for longer than
    /// <see cref="RestoReconciliationOptions.UnacknowledgedAlertMinutes"/>. The threshold is server-side; clients do not send a time.
    /// </summary>
    Task<RestoKeysetPageDto<UnacknowledgedTicketRowDto>> ListUnacknowledgedTicketsAsync(
        Guid? branchId, Guid? afterItemId, int? limit, CancellationToken cancellationToken = default);
}

public sealed class RestoReleaseQueryService : IRestoReleaseQueryService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly RestoReconciliationOptions _options;
    private readonly TimeProvider _timeProvider;

    public RestoReleaseQueryService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IBranchAccessResolver branchAccess,
        RestoReconciliationOptions options,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _options = options;
        _timeProvider = timeProvider;
    }

    public async Task<RestoKeysetPageDto<PendingPayoReleaseRowDto>> ListPendingReleasesAsync(
        Guid? branchId, Guid? afterOrderId, int? limit, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var scope = await ResolveBranchScopeAsync(branchId, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var page = await RestoReleaseQueries.PendingPageAsync(_db, tenantId, scope, afterOrderId, ClampLimit(limit), cancellationToken);
        var rows = page.Rows
            .Select(r => new PendingPayoReleaseRowDto(r.OrderId, r.RoundId, r.BranchId, r.SettledAtUtc, AgeSeconds(now, r.SettledAtUtc)))
            .ToList();
        return new RestoKeysetPageDto<PendingPayoReleaseRowDto>(rows, page.HasMore ? page.LastKey : null);
    }

    public async Task<RestoKeysetPageDto<UnacknowledgedTicketRowDto>> ListUnacknowledgedTicketsAsync(
        Guid? branchId, Guid? afterItemId, int? limit, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var scope = await ResolveBranchScopeAsync(branchId, cancellationToken);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var unacknowledgedBefore = now.AddMinutes(-_options.UnacknowledgedAlertMinutes);

        var page = await RestoReleaseQueries.UnacknowledgedPageAsync(
            _db, tenantId, scope, unacknowledgedBefore, afterItemId, ClampLimit(limit), cancellationToken);
        var rows = page.Rows
            .Select(t => new UnacknowledgedTicketRowDto(
                t.ItemId, t.OrderId, t.RoundId, t.BranchId, t.StationName, t.ProductName, t.Quantity, t.ReleasedAtUtc,
                AgeSeconds(now, t.ReleasedAtUtc)))
            .ToList();
        return new RestoKeysetPageDto<UnacknowledgedTicketRowDto>(rows, page.HasMore ? page.LastKey : null);
    }

    /// <summary>
    /// Branch-assigned users see only their branch. A requested branch that differs from the assignment is a 404, matching
    /// the order endpoints' isolation rule.
    /// </summary>
    private async Task<Guid?> ResolveBranchScopeAsync(Guid? requestedBranchId, CancellationToken cancellationToken)
    {
        var assigned = await _branchAccess.AssignedBranchIdAsync(cancellationToken);
        if (assigned is { } owned && requestedBranchId is { } requested && requested != owned)
        {
            throw new NotFoundException(ErrorCodes.RestoOrderNotFound, "Branch not found.");
        }

        return assigned ?? requestedBranchId;
    }

    private static int ClampLimit(int? limit) => limit is null or <= 0 ? DefaultLimit : Math.Min(limit.Value, MaxLimit);

    private static long AgeSeconds(DateTime now, DateTime since) => Math.Max(0, (long)(now - since).TotalSeconds);

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
