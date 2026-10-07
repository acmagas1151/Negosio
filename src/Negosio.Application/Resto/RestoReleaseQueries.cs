using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

/// <summary>
/// Tenant-explicit queries for PAYO release and kitchen-ticket reporting. Both the worker and the list endpoints use these
/// definitions, so what the alert reports is exactly what the worker is responsible for.
/// </summary>
/// <remarks>
/// Paging is keyset-based: the caller passes the last row it saw and receives rows strictly after it, ordered by a stable
/// key, and every page is bounded by the caller's limit. Filters are applied in SQL; rows are mapped to records only after
/// the database has returned them.
/// </remarks>
internal static class RestoReleaseQueries
{
    /// <summary>A settled Pay-as-you-order round still in <c>Draft</c> with at least one billable item.</summary>
    internal sealed record PendingRow(Guid OrderId, Guid RoundId, Guid BranchId, DateTime SettledAtUtc);

    /// <summary>A released item whose kitchen status is still <c>Pending</c>: available to the kitchen but unacknowledged.</summary>
    internal sealed record TicketRow(
        Guid ItemId, Guid OrderId, Guid RoundId, Guid BranchId, string StationName, string ProductName, decimal Quantity, DateTime ReleasedAtUtc);

    /// <summary>
    /// Due rounds for the worker: settled before the grace cutoff and after the cursor, oldest first. Backoff is applied by
    /// the caller in memory, so the SQL stays a plain keyset page of any size.
    /// </summary>
    public static async Task<List<PendingRow>> DueForReleaseAsync(
        ITenantDbContext db,
        Guid tenantId,
        DateTime settledBeforeUtc,
        (DateTime SettledAtUtc, Guid OrderId)? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var query =
            from o in db.RestoOrders.AsNoTracking()
            join r in db.RestoOrderRounds.AsNoTracking() on o.Id equals r.RestoOrderId
            where o.TenantId == tenantId
                && o.ServiceType == RestoServiceType.PayAsYouOrder
                && o.Status == RestoOrderStatus.Settled
                && o.SettledAtUtc != null
                && o.SettledAtUtc <= settledBeforeUtc
                && r.Status == RestoOrderRoundStatus.Draft
                && db.RestoOrderItems.Any(i => i.RestoOrderRoundId == r.Id && i.VoidedAtUtc == null)
            select new { OrderId = o.Id, RoundId = r.Id, o.BranchId, o.SettledAtUtc };

        if (cursor is { } c)
        {
            query = query.Where(x => x.SettledAtUtc > c.SettledAtUtc || (x.SettledAtUtc == c.SettledAtUtc && x.OrderId > c.OrderId));
        }

        var rows = await query
            .OrderBy(x => x.SettledAtUtc).ThenBy(x => x.OrderId)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return rows.Select(x => new PendingRow(x.OrderId, x.RoundId, x.BranchId, x.SettledAtUtc!.Value)).ToList();
    }

    /// <summary>One bounded page of pending releases for a branch scope (null = whole tenant). Oldest first.</summary>
    public static async Task<KeysetPage<PendingRow, Guid>> PendingPageAsync(
        ITenantDbContext db,
        Guid tenantId,
        Guid? branchScope,
        Guid? afterOrderId,
        int limit,
        CancellationToken cancellationToken)
    {
        var query =
            from o in db.RestoOrders.AsNoTracking()
            join r in db.RestoOrderRounds.AsNoTracking() on o.Id equals r.RestoOrderId
            where o.TenantId == tenantId
                && o.ServiceType == RestoServiceType.PayAsYouOrder
                && o.Status == RestoOrderStatus.Settled
                && o.SettledAtUtc != null
                && r.Status == RestoOrderRoundStatus.Draft
                && db.RestoOrderItems.Any(i => i.RestoOrderRoundId == r.Id && i.VoidedAtUtc == null)
            select new { OrderId = o.Id, RoundId = r.Id, o.BranchId, o.SettledAtUtc };

        if (branchScope is { } scope)
        {
            query = query.Where(x => x.BranchId == scope);
        }

        if (afterOrderId is { } after)
        {
            var anchor = await db.RestoOrders.AsNoTracking()
                .Where(o => o.TenantId == tenantId && o.Id == after)
                .Select(o => o.SettledAtUtc)
                .SingleOrDefaultAsync(cancellationToken);
            if (anchor is null)
            {
                return new KeysetPage<PendingRow, Guid>([], HasMore: false, LastKey: null);
            }

            var settled = anchor.Value;
            query = query.Where(x => x.SettledAtUtc > settled || (x.SettledAtUtc == settled && x.OrderId > after));
        }

        var fetched = await query
            .OrderBy(x => x.SettledAtUtc).ThenBy(x => x.OrderId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = fetched.Count > limit;
        var page = fetched.Take(limit)
            .Select(x => new PendingRow(x.OrderId, x.RoundId, x.BranchId, x.SettledAtUtc!.Value))
            .ToList();
        return new KeysetPage<PendingRow, Guid>(page, hasMore, page.Count == 0 ? null : page[^1].OrderId);
    }

    /// <summary>Released-but-unacknowledged items whose round was released at or before <paramref name="unacknowledgedBeforeUtc"/>.</summary>
    public static async Task<KeysetPage<TicketRow, Guid>> UnacknowledgedPageAsync(
        ITenantDbContext db,
        Guid tenantId,
        Guid? branchScope,
        DateTime unacknowledgedBeforeUtc,
        Guid? afterItemId,
        int limit,
        CancellationToken cancellationToken)
    {
        var query =
            from i in db.RestoOrderItems.AsNoTracking()
            join r in db.RestoOrderRounds.AsNoTracking() on i.RestoOrderRoundId equals r.Id
            join o in db.RestoOrders.AsNoTracking() on r.RestoOrderId equals o.Id
            where i.TenantId == tenantId
                && r.Status == RestoOrderRoundStatus.Released
                && r.ReleasedAtUtc != null
                && r.ReleasedAtUtc <= unacknowledgedBeforeUtc
                && i.KitchenStatus == RestoKitchenStatus.Pending
                && i.VoidedAtUtc == null
            select new
            {
                ItemId = i.Id,
                OrderId = o.Id,
                RoundId = r.Id,
                o.BranchId,
                StationName = i.StationNameSnapshot,
                ProductName = i.ProductNameSnapshot,
                i.Quantity,
                r.ReleasedAtUtc,
            };

        if (branchScope is { } scope)
        {
            query = query.Where(x => x.BranchId == scope);
        }

        if (afterItemId is { } after)
        {
            // Look the anchor up without the Pending filter: it may have been acknowledged since the last page.
            var anchor = await (
                from i in db.RestoOrderItems.AsNoTracking()
                join r in db.RestoOrderRounds.AsNoTracking() on i.RestoOrderRoundId equals r.Id
                where i.TenantId == tenantId && i.Id == after && r.ReleasedAtUtc != null
                select r.ReleasedAtUtc
            ).SingleOrDefaultAsync(cancellationToken);
            if (anchor is null)
            {
                return new KeysetPage<TicketRow, Guid>([], HasMore: false, LastKey: null);
            }

            var released = anchor.Value;
            query = query.Where(x => x.ReleasedAtUtc > released || (x.ReleasedAtUtc == released && x.ItemId > after));
        }

        var fetched = await query
            .OrderBy(x => x.ReleasedAtUtc).ThenBy(x => x.ItemId)
            .Take(limit + 1)
            .ToListAsync(cancellationToken);

        var hasMore = fetched.Count > limit;
        var page = fetched.Take(limit)
            .Select(x => new TicketRow(x.ItemId, x.OrderId, x.RoundId, x.BranchId, x.StationName, x.ProductName, x.Quantity, x.ReleasedAtUtc!.Value))
            .ToList();
        return new KeysetPage<TicketRow, Guid>(page, hasMore, page.Count == 0 ? null : page[^1].ItemId);
    }

    public static Task<int> CountUnacknowledgedAsync(ITenantDbContext db, Guid tenantId, DateTime unacknowledgedBeforeUtc, CancellationToken cancellationToken) =>
        (from i in db.RestoOrderItems.AsNoTracking()
         join r in db.RestoOrderRounds.AsNoTracking() on i.RestoOrderRoundId equals r.Id
         where i.TenantId == tenantId
             && r.Status == RestoOrderRoundStatus.Released
             && r.ReleasedAtUtc != null
             && r.ReleasedAtUtc <= unacknowledgedBeforeUtc
             && i.KitchenStatus == RestoKitchenStatus.Pending
             && i.VoidedAtUtc == null
         select i.Id).CountAsync(cancellationToken);
}

/// <summary>One page of a keyset-paged list. <see cref="LastKey"/> is the cursor for the next page when <see cref="HasMore"/>.</summary>
internal sealed record KeysetPage<TRow, TKey>(IReadOnlyList<TRow> Rows, bool HasMore, TKey? LastKey) where TKey : struct;
