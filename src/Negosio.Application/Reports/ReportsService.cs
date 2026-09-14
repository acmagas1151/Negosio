using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Delivery;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Reports;

/// <summary>
/// Every report query here is SQL aggregation (SUM/COUNT/GROUP BY) over the real, persisted Sale /
/// SaleItem / Payment / SaleReturn tables — never a raw row list materialized into memory. Formulas:
///
///  - Gross sales    = Σ Sale.Subtotal            (before discount, before tax)
///  - Discounts      = Σ Sale.DiscountTotal
///  - Tax            = Σ Sale.TaxTotal
///  - Net sales      = Σ Sale.GrandTotal           (after discount/tax, before returns; as of the
///                      DeliveryCharge feature this also includes any delivery fee — GrandTotal is
///                      saleTotal + DeliveryCharge — this was not a deliberate revenue-classification
///                      decision and may need revisiting)
///  - Returns        = Σ SaleReturn.TotalRefund, attributed to the RETURN's own date, not the
///                      original sale's date
///  - Net collected  = Net sales − Returns
///
/// All five sale-side totals only ever include Status != Voided — a voided sale's original amounts
/// stay on the row for audit but must never inflate a normal total. Voided sales get their own
/// count/value, attributed to VoidedAtUtc. A fully-refunded sale stays in Gross/Net at its original
/// value and is offset by its Return, by design — it never silently disappears from history.
/// </summary>
public sealed class ReportsService : IReportsService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IReportPeriodResolver _periodResolver;

    public ReportsService(
        ITenantDbContext db, ICurrentUser currentUser, IBranchAccessResolver branchAccess, IReportPeriodResolver periodResolver)
    {
        _db = db;
        _currentUser = currentUser;
        _branchAccess = branchAccess;
        _periodResolver = periodResolver;
    }

    public async Task<ReportsOverviewDto> GetOverviewAsync(ReportFilter filter, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var range = _periodResolver.Resolve(filter.Period, filter.FromDate, filter.ToDate);

        var salesBase = await BaseSalesAsync(filter, cancellationToken);
        var returnsBase = await BaseReturnsAsync(filter, cancellationToken);

        var current = await ComputeKpisAsync(salesBase, returnsBase, range.FromUtc, range.ToUtc, cancellationToken);
        var previous = await ComputeKpisAsync(salesBase, returnsBase, range.PreviousFromUtc, range.PreviousToUtc, cancellationToken);

        var comparison = new ReportKpiComparisonDto(
            PercentChange(previous.GrossSales, current.GrossSales),
            PercentChange(previous.NetSales, current.NetSales),
            PercentChange(previous.CompletedTransactions, current.CompletedTransactions),
            PercentChange(previous.AverageTransactionValue, current.AverageTransactionValue),
            PercentChange(previous.Returns, current.Returns));

        var trend = await ComputeTrendAsync(salesBase, range, cancellationToken);
        var paymentMethods = await ComputePaymentMethodsAsync(salesBase, range.FromUtc, range.ToUtc, cancellationToken);

        return new ReportsOverviewDto(range.FromUtc, range.ToUtc, current, comparison, range.Hourly, trend, paymentMethods);
    }

    public async Task<IReadOnlyList<TopProductDto>> GetTopProductsAsync(
        ReportFilter filter, int top, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var range = _periodResolver.Resolve(filter.Period, filter.FromDate, filter.ToDate);
        var qualifying = Qualifying(await BaseSalesAsync(filter, cancellationToken), range.FromUtc, range.ToUtc);
        var boundedTop = Math.Clamp(top, 1, 50);

        var agg = await (from i in _db.SaleItems.AsNoTracking()
                          join s in qualifying on i.SaleId equals s.Id
                          group i by i.ProductVariantId into g
                          select new { ProductVariantId = g.Key, Qty = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.NetAmount) })
            .OrderByDescending(x => x.Amount)
            .Take(boundedTop)
            .ToListAsync(cancellationToken);

        if (agg.Count == 0)
        {
            return Array.Empty<TopProductDto>();
        }

        // Live join for display — the current catalog name/SKU/category, not a point-in-time
        // snapshot (SaleItem's own snapshot has no CategoryId to group by in the first place).
        var variantIds = agg.Select(a => a.ProductVariantId).ToList();
        var variants = await _db.ProductVariants.AsNoTracking()
            .Where(v => variantIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Name, v.Sku, v.IsDefault, v.ProductId })
            .ToListAsync(cancellationToken);

        var productIds = variants.Select(v => v.ProductId).Distinct().ToList();
        var products = await _db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.CategoryId })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        var categoryIds = products.Values.Select(p => p.CategoryId).Distinct().ToList();
        var categoryNames = await _db.Categories.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var variantById = variants.ToDictionary(v => v.Id);

        return agg.Select(a =>
        {
            variantById.TryGetValue(a.ProductVariantId, out var variant);
            var product = variant is not null ? products.GetValueOrDefault(variant.ProductId) : null;
            var categoryName = product is not null ? categoryNames.GetValueOrDefault(product.CategoryId) : null;

            return new TopProductDto(
                a.ProductVariantId,
                product?.Name ?? variant?.Name ?? "(deleted product)",
                variant is { IsDefault: false } ? variant.Name : null,
                variant?.Sku,
                categoryName,
                a.Qty,
                a.Amount);
        }).ToList();
    }

    public async Task<IReadOnlyList<CategoryPerformanceDto>> GetCategoryPerformanceAsync(
        ReportFilter filter, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var range = _periodResolver.Resolve(filter.Period, filter.FromDate, filter.ToDate);
        var qualifying = Qualifying(await BaseSalesAsync(filter, cancellationToken), range.FromUtc, range.ToUtc);

        var agg = await (from i in _db.SaleItems.AsNoTracking()
                          join s in qualifying on i.SaleId equals s.Id
                          join v in _db.ProductVariants.AsNoTracking() on i.ProductVariantId equals v.Id
                          join p in _db.Products.AsNoTracking() on v.ProductId equals p.Id
                          group i by p.CategoryId into g
                          select new { CategoryId = (Guid?)g.Key, Qty = g.Sum(x => x.Quantity), Amount = g.Sum(x => x.NetAmount) })
            .ToListAsync(cancellationToken);

        var totalAmount = agg.Sum(a => a.Amount);
        var categoryIds = agg.Where(a => a.CategoryId is not null).Select(a => a.CategoryId!.Value).Distinct().ToList();
        var categoryNames = await _db.Categories.AsNoTracking()
            .Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        return agg
            .OrderByDescending(a => a.Amount)
            .Select(a => new CategoryPerformanceDto(
                a.CategoryId,
                a.CategoryId is { } cid ? categoryNames.GetValueOrDefault(cid, "(unknown category)") : "(uncategorized)",
                a.Qty,
                a.Amount,
                totalAmount == 0m ? 0m : Math.Round(a.Amount / totalAmount * 100m, 1, MidpointRounding.AwayFromZero)))
            .ToList();
    }

    public async Task<DeliveryReportResultDto> GetDeliveriesAsync(DeliveryReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow + ReportPeriodResolver.BusinessOffset);

        // Deliveries only. The schedule table is now shared with pickups (discriminated by Method), so
        // this filter is what keeps the shipped delivery report meaning exactly what it meant before
        // pickups existed. The pickup and combined views are a separate task.
        var receipts = _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.SaleId != null && d.Method == FulfillmentMethod.Delivery);
        if (branchFilter is { } b)
        {
            receipts = receipts.Where(d => d.BranchId == b);
        }

        var (effectiveFrom, effectiveTo, effectiveStatus, overdueOnly) = ResolveDeliveryPreset(query, todayLocal);
        if (effectiveFrom is { } from) receipts = receipts.Where(d => d.ScheduledDate >= from);
        if (effectiveTo is { } to) receipts = receipts.Where(d => d.ScheduledDate <= to);
        if (effectiveStatus is { } status) receipts = receipts.Where(d => d.Status == status);
        if (overdueOnly) receipts = receipts.Where(d => d.Status == FulfillmentStatus.Pending && d.ScheduledDate < todayLocal);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            receipts = receipts.Where(d =>
                (d.RelatedSaleNumber != null && d.RelatedSaleNumber.Contains(term)) ||
                d.RecipientName.Contains(term) ||
                (d.DeliveryAddress != null && d.DeliveryAddress.Contains(term)) ||
                (d.ContactNumber != null && d.ContactNumber.Contains(term)));
        }

        var joined =
            from dr in receipts
            join s in _db.Sales.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != SaleStatus.Voided) on dr.SaleId equals s.Id
            select new { dr, s };

        // Fix: aggregate charge-related totals over DISTINCT sales, never per joined DR row — a sale
        // with N schedules must contribute its DeliveryCharge exactly once (see Global Constraints).
        var distinctSales = joined.Select(x => new { x.s.Id, x.s.DeliveryCharge }).Distinct();
        var distinctSalesCount = await distinctSales.CountAsync(cancellationToken);
        var freeSalesCount = await distinctSales.CountAsync(x => x.DeliveryCharge == 0m, cancellationToken);
        var totalCharges = distinctSalesCount == 0 ? 0m : await distinctSales.SumAsync(x => x.DeliveryCharge, cancellationToken);
        var averageChargePerSale = distinctSalesCount == 0 ? 0m : Money.Round(totalCharges / distinctSalesCount);
        var totalSchedules = await joined.CountAsync(cancellationToken);

        var totals = new DeliveryReportTotalsDto(
            totalSchedules, distinctSalesCount, freeSalesCount, distinctSalesCount - freeSalesCount, totalCharges, averageChargePerSale);

        // Recommended ordering: Pending first, earliest scheduled date, newest-created tiebreak.
        var projected = joined
            .OrderBy(x => x.dr.Status == FulfillmentStatus.Pending ? 0 : 1)
            .ThenBy(x => x.dr.ScheduledDate)
            .ThenByDescending(x => x.dr.CreatedAtUtc)
            .Select(x => new DeliveryReportRow(
                x.dr.Id, x.s.Id, x.s.SaleNumber, x.dr.SequenceNumber, x.dr.ScheduledDate, x.dr.Status,
                x.dr.Status == FulfillmentStatus.Pending && x.dr.ScheduledDate < todayLocal,
                // DeliveryAddress is nullable on the shared schedule entity (a pickup has none) but is
                // always present on a Delivery row, which is all this query selects.
                x.dr.CreatedAtUtc, x.dr.RecipientName, x.dr.DeliveryAddress ?? string.Empty, x.dr.ContactNumber, x.dr.Notes,
                x.s.DeliveryCharge, x.s.GrandTotal, x.dr.PreparedByNameSnapshot,
                x.dr.CompletedAtUtc, x.dr.CancelledAtUtc, x.dr.CancellationReason,
                _db.Payments.Where(p => p.SaleId == x.s.Id).Select(p => p.Method).Distinct().ToList()));

        var rows = await PagedResult<DeliveryReportRow>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = rows.Items.Select(r => new DeliveryReportRowDto(
            r.DeliveryReceiptId, r.SaleId, r.SaleNumber, r.SequenceNumber, r.ScheduledDeliveryDate, r.Status, r.IsOverdue,
            r.CreatedAtUtc, r.RecipientName, r.DeliveryAddress, r.ContactNumber, r.DeliveryNotes, r.DeliveryCharge, r.SaleGrandTotal,
            string.Join(" + ", r.Methods.Select(m => m.ToString())), r.PreparedByName, r.DeliveredAtUtc, r.CancelledAtUtc, r.CancellationReason))
            .ToList();

        return new DeliveryReportResultDto(
            new PagedResult<DeliveryReportRowDto>(items, rows.Page, rows.PageSize, rows.TotalCount, rows.TotalPages),
            totals);
    }

    private static (DateOnly? From, DateOnly? To, FulfillmentStatus? Status, bool OverdueOnly) ResolveDeliveryPreset(
        DeliveryReportQuery query, DateOnly todayLocal) => query.Preset switch
    {
        DeliveryReportPreset.Today => (todayLocal, todayLocal, FulfillmentStatus.Pending, false),
        DeliveryReportPreset.Upcoming => (todayLocal.AddDays(1), null, FulfillmentStatus.Pending, false),
        DeliveryReportPreset.Overdue => (null, null, null, true),
        DeliveryReportPreset.NeedsRescheduling => (null, null, null, true),
        DeliveryReportPreset.Delivered => (null, null, FulfillmentStatus.Completed, false),
        DeliveryReportPreset.Cancelled => (null, null, FulfillmentStatus.Cancelled, false),
        _ => (query.FromDate, query.ToDate, query.Status, false),
    };

    private sealed record DeliveryReportRow(
        Guid DeliveryReceiptId, Guid SaleId, string SaleNumber, int SequenceNumber, DateOnly ScheduledDeliveryDate,
        FulfillmentStatus Status, bool IsOverdue, DateTime CreatedAtUtc,
        string RecipientName, string DeliveryAddress, string? ContactNumber, string? DeliveryNotes,
        decimal DeliveryCharge, decimal SaleGrandTotal, string PreparedByName,
        DateTime? DeliveredAtUtc, DateTime? CancelledAtUtc, string? CancellationReason, List<PaymentMethod> Methods);

    /// <summary>Pickup's counterpart to <see cref="GetDeliveriesAsync"/> — same filter plumbing, same
    /// paging, same branch scoping, filtered to <see cref="FulfillmentMethod.Pickup"/> instead. The two
    /// differences from the delivery report: no address column (a pickup transports nothing) and
    /// <see cref="PickupReportRowDto.DeliveryCharge"/> is hardcoded to 0 rather than read from the Sale —
    /// a pickup never carries one, even when the same sale's delivery side does.</summary>
    public async Task<PickupReportResultDto> GetPickupsAsync(PickupReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow + ReportPeriodResolver.BusinessOffset);

        var receipts = _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.SaleId != null && d.Method == FulfillmentMethod.Pickup);
        if (branchFilter is { } b)
        {
            receipts = receipts.Where(d => d.BranchId == b);
        }

        var (effectiveFrom, effectiveTo, effectiveStatus, overdueOnly) = ResolvePickupPreset(query, todayLocal);
        if (effectiveFrom is { } from) receipts = receipts.Where(d => d.ScheduledDate >= from);
        if (effectiveTo is { } to) receipts = receipts.Where(d => d.ScheduledDate <= to);
        if (effectiveStatus is { } status) receipts = receipts.Where(d => d.Status == status);
        if (overdueOnly) receipts = receipts.Where(d => d.Status == FulfillmentStatus.Pending && d.ScheduledDate < todayLocal);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            receipts = receipts.Where(d =>
                (d.RelatedSaleNumber != null && d.RelatedSaleNumber.Contains(term)) ||
                d.RecipientName.Contains(term) ||
                (d.ContactNumber != null && d.ContactNumber.Contains(term)));
        }

        var joined =
            from dr in receipts
            join s in _db.Sales.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != SaleStatus.Voided) on dr.SaleId equals s.Id
            select new { dr, s };

        var distinctSales = joined.Select(x => new { x.s.Id }).Distinct();
        var distinctSalesCount = await distinctSales.CountAsync(cancellationToken);
        var totalSchedules = await joined.CountAsync(cancellationToken);

        var totals = new PickupReportTotalsDto(totalSchedules, distinctSalesCount);

        var projected = joined
            .OrderBy(x => x.dr.Status == FulfillmentStatus.Pending ? 0 : 1)
            .ThenBy(x => x.dr.ScheduledDate)
            .ThenByDescending(x => x.dr.CreatedAtUtc)
            .Select(x => new PickupReportRow(
                x.dr.Id, x.s.Id, x.s.SaleNumber, x.dr.SequenceNumber, x.dr.ScheduledDate, x.dr.Status,
                x.dr.Status == FulfillmentStatus.Pending && x.dr.ScheduledDate < todayLocal,
                x.dr.CreatedAtUtc, x.dr.RecipientName, x.dr.ContactNumber, x.dr.Notes,
                x.s.GrandTotal, x.dr.PreparedByNameSnapshot,
                x.dr.CompletedAtUtc, x.dr.CancelledAtUtc, x.dr.CancellationReason,
                _db.Payments.Where(p => p.SaleId == x.s.Id).Select(p => p.Method).Distinct().ToList()));

        var rows = await PagedResult<PickupReportRow>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = rows.Items.Select(r => new PickupReportRowDto(
            r.DeliveryReceiptId, r.SaleId, r.SaleNumber, r.SequenceNumber, r.ScheduledPickupDate, r.Status, r.IsOverdue,
            r.CreatedAtUtc, r.RecipientName, r.ContactNumber, r.Notes,
            DeliveryCharge: 0m, // never read from the Sale — a pickup never carries one.
            r.SaleGrandTotal, string.Join(" + ", r.Methods.Select(m => m.ToString())), r.PreparedByName,
            r.CompletedAtUtc, r.CancelledAtUtc, r.CancellationReason))
            .ToList();

        return new PickupReportResultDto(
            new PagedResult<PickupReportRowDto>(items, rows.Page, rows.PageSize, rows.TotalCount, rows.TotalPages),
            totals);
    }

    private static (DateOnly? From, DateOnly? To, FulfillmentStatus? Status, bool OverdueOnly) ResolvePickupPreset(
        PickupReportQuery query, DateOnly todayLocal) => query.Preset switch
    {
        PickupReportPreset.Today => (todayLocal, todayLocal, FulfillmentStatus.Pending, false),
        PickupReportPreset.Upcoming => (todayLocal.AddDays(1), null, FulfillmentStatus.Pending, false),
        PickupReportPreset.Overdue => (null, null, null, true),
        PickupReportPreset.Pending => (null, null, FulfillmentStatus.Pending, false),
        PickupReportPreset.Claimed => (null, null, FulfillmentStatus.Completed, false),
        PickupReportPreset.Cancelled => (null, null, FulfillmentStatus.Cancelled, false),
        _ => (query.FromDate, query.ToDate, query.Status, false),
    };

    private sealed record PickupReportRow(
        Guid DeliveryReceiptId, Guid SaleId, string SaleNumber, int SequenceNumber, DateOnly ScheduledPickupDate,
        FulfillmentStatus Status, bool IsOverdue, DateTime CreatedAtUtc,
        string RecipientName, string? ContactNumber, string? Notes,
        decimal SaleGrandTotal, string PreparedByName,
        DateTime? CompletedAtUtc, DateTime? CancelledAtUtc, string? CancellationReason, List<PaymentMethod> Methods);

    /// <summary>
    /// The combined allocation view: one row per sale-item-per-method-per-status "bucket" that actually
    /// holds quantity, across BOTH fulfillment methods, plus a synthesized Take-now row and synthesized
    /// Unscheduled rows for intent that has no schedule yet, plus a row for every CANCELLED schedule (the
    /// report's cancellation/conversion history requirement) carrying a <c>ReplacementScheduleId</c> when
    /// the cancellation produced one. See <see cref="FulfillmentReportRowDto"/>'s doc comment for exactly
    /// what each row means.
    /// <para><b>Why the delivery-charge total can never be multiplied here</b> (the bug this whole task is
    /// most at risk of reintroducing, per the plan's Global Constraints): <paramref name="query"/>'s
    /// qualifying-sale set below is materialized as <c>sales</c> — ONE row per Sale, selected directly off
    /// the <c>Sales</c> table with no join to <c>DeliveryReceipts</c> at all. Schedules are loaded
    /// separately (keyed by SaleItemId) and never joined back onto <c>sales</c>. So <c>TotalDeliveryCharges</c>,
    /// computed as <c>sales.Sum(s => s.DeliveryCharge)</c>, sums exactly one DeliveryCharge per sale no
    /// matter how many delivery or pickup schedules that sale has — there is no per-schedule row for a
    /// join to multiply it by. This is structurally stronger than a post-hoc <c>.Distinct()</c>: the
    /// duplication this class of bug depends on (one row per schedule, each carrying the sale's charge)
    /// never exists here to begin with.</para>
    /// </summary>
    public async Task<FulfillmentReportResultDto> GetFulfillmentAsync(
        FulfillmentReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow + ReportPeriodResolver.BusinessOffset);

        // Every line with ANY post-checkout intent (delivery or pickup) on a non-voided sale — same
        // qualifying filter GetSaleFulfillmentAsync/GetDeliveryFulfillmentAsync use. A sale that is
        // entirely take-now has nothing to track and never appears here.
        var qualifyingSales = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status != SaleStatus.Voided
            && _db.SaleItems.Any(i => i.SaleId == s.Id && (i.DeliveryRequiredQuantity > 0m || i.PickupRequiredQuantity > 0m)));
        if (branchFilter is { } b)
        {
            qualifyingSales = qualifyingSales.Where(s => s.BranchId == b);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            qualifyingSales = qualifyingSales.Where(s => s.SaleNumber.Contains(term));
        }

        // ONE row per sale, never joined to a schedule table — see this method's doc comment.
        var sales = await qualifyingSales
            .Select(s => new { s.Id, s.SaleNumber, s.DeliveryCharge })
            .ToListAsync(cancellationToken);

        if (sales.Count == 0)
        {
            var emptySummary = new FulfillmentReportSummaryDto(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0, 0, 0, 0m);
            var (emptyPage, emptyPageSize) = PagedResult<FulfillmentReportRowDto>.Normalize(query.Page, query.PageSize);
            return new FulfillmentReportResultDto(
                new PagedResult<FulfillmentReportRowDto>(Array.Empty<FulfillmentReportRowDto>(), emptyPage, emptyPageSize, 0, 0),
                emptySummary);
        }

        var saleIds = sales.Select(s => s.Id).ToList();
        var saleById = sales.ToDictionary(s => s.Id);

        var items = await _db.SaleItems.AsNoTracking()
            .Where(i => saleIds.Contains(i.SaleId) && (i.DeliveryRequiredQuantity > 0m || i.PickupRequiredQuantity > 0m))
            .Select(i => new
            {
                i.Id,
                i.SaleId,
                i.ProductNameSnapshot,
                i.VariantNameSnapshot,
                i.Quantity,
                i.DeliveryRequiredQuantity,
                i.PickupRequiredQuantity,
            })
            .ToListAsync(cancellationToken);
        var itemIds = items.Select(i => i.Id).ToList();

        // Non-cancelled schedules only — this is the set every QUANTITY total below is computed from
        // (Pending/Completed sums, and therefore Unscheduled = intent minus these). Cancelled schedules
        // must never contribute to a quantity total (their released/converted quantity already shows up
        // in the Unscheduled or replacement-method figures instead), which is why this query stays
        // Cancelled-excluding exactly as before.
        var scheduleRows = await (
            from dri in _db.DeliveryReceiptItems.AsNoTracking()
            join dr in _db.DeliveryReceipts.AsNoTracking() on dri.DeliveryReceiptId equals dr.Id
            where itemIds.Contains(dri.SaleItemId) && dr.Status != FulfillmentStatus.Cancelled
            select new
            {
                dri.SaleItemId,
                dri.Quantity,
                ScheduleId = dr.Id,
                dr.Method,
                dr.Status,
                dr.ScheduledDate,
                dr.CompletedAtUtc,
                dr.RecipientName,
            })
            .ToListAsync(cancellationToken);
        var scheduleRowsByItem = scheduleRows.ToLookup(r => r.SaleItemId);

        // Cancelled schedules, queried SEPARATELY, purely to surface their own history ROWS — the spec
        // requires "cancellation and conversion history" in this report's row set. Never merged into
        // scheduleRows and never fed into any total* accumulator below: only the row-building loop reads
        // this, and a cancelled schedule's quantity is deliberately never added to a summary total.
        var cancelledScheduleRows = await (
            from dri in _db.DeliveryReceiptItems.AsNoTracking()
            join dr in _db.DeliveryReceipts.AsNoTracking() on dri.DeliveryReceiptId equals dr.Id
            where itemIds.Contains(dri.SaleItemId) && dr.Status == FulfillmentStatus.Cancelled
            select new
            {
                dri.SaleItemId,
                dri.Quantity,
                ScheduleId = dr.Id,
                dr.Method,
                dr.Status,
                dr.ScheduledDate,
                dr.CompletedAtUtc,
                dr.RecipientName,
            })
            .ToListAsync(cancellationToken);
        var cancelledScheduleRowsByItem = cancelledScheduleRows.ToLookup(r => r.SaleItemId);

        // What each cancelled schedule became, if anything — FulfillmentConversion already carries this
        // (SourceRecordId = the cancelled schedule, ReplacementRecordId = the new schedule it produced,
        // written transactionally by DeliveryReceiptService on every cancellation) so this is a lookup,
        // never a re-derivation. A cancellation writes one conversion row PER sale item it touched, all
        // sharing the same ReplacementRecordId for a given SourceRecordId, so grouping and taking any one
        // is safe.
        var cancelledScheduleIds = cancelledScheduleRows.Select(r => r.ScheduleId).Distinct().ToList();
        var replacementBySource = cancelledScheduleIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : (await _db.FulfillmentConversions.AsNoTracking()
                .Where(c => c.SourceRecordId != null && cancelledScheduleIds.Contains(c.SourceRecordId.Value))
                .Select(c => new { SourceRecordId = c.SourceRecordId!.Value, c.ReplacementRecordId })
                .ToListAsync(cancellationToken))
                .GroupBy(c => c.SourceRecordId)
                .ToDictionary(g => g.Key, g => g.First().ReplacementRecordId);

        // A schedule count (records, not units) across every method for the qualifying sales — this is
        // the report's own "cancelled schedules" total. Deliberately its own COUNT query rather than
        // `cancelledScheduleRows.Count` (which counts ROWS, i.e. one per sale item a cancelled schedule
        // touched — a schedule spanning two sale items would otherwise be counted twice).
        var cancelledSchedulesCount = await _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.SaleId != null && saleIds.Contains(d.SaleId!.Value) && d.Status == FulfillmentStatus.Cancelled)
            .CountAsync(cancellationToken);

        var rows = new List<FulfillmentReportRowDto>();
        decimal totalTakeNow = 0m, totalDeliveryUnscheduled = 0m, totalDeliveryPending = 0m, totalDelivered = 0m,
            totalPickupUnscheduled = 0m, totalPickupPending = 0m, totalClaimed = 0m;

        // Per-sale running totals, fed into SaleFulfillmentCalculator.Derive exactly as
        // GetSaleFulfillmentAsync feeds it — same inputs, same function, so the two views can never
        // disagree about a sale's status.
        var perSale = new Dictionary<Guid, (decimal Sold, decimal TakeNow, decimal Delivered, decimal Claimed,
            decimal DeliveryPending, decimal PickupPending, decimal DeliveryUnscheduled, decimal PickupUnscheduled, bool Overdue)>();

        foreach (var item in items)
        {
            var saleNumber = saleById[item.SaleId].SaleNumber;
            var scheduleForItem = scheduleRowsByItem[item.Id].ToList();

            var deliveryPending = scheduleForItem
                .Where(r => r.Method == FulfillmentMethod.Delivery && r.Status == FulfillmentStatus.Pending).Sum(r => r.Quantity);
            var deliveryCompleted = scheduleForItem
                .Where(r => r.Method == FulfillmentMethod.Delivery && r.Status == FulfillmentStatus.Completed).Sum(r => r.Quantity);
            var pickupPending = scheduleForItem
                .Where(r => r.Method == FulfillmentMethod.Pickup && r.Status == FulfillmentStatus.Pending).Sum(r => r.Quantity);
            var pickupCompleted = scheduleForItem
                .Where(r => r.Method == FulfillmentMethod.Pickup && r.Status == FulfillmentStatus.Completed).Sum(r => r.Quantity);

            var deliveryUnscheduled = item.DeliveryRequiredQuantity - deliveryPending - deliveryCompleted;
            var pickupUnscheduled = item.PickupRequiredQuantity - pickupPending - pickupCompleted;
            var takeNow = item.Quantity - item.DeliveryRequiredQuantity - item.PickupRequiredQuantity;

            if (takeNow > 0m)
            {
                rows.Add(new FulfillmentReportRowDto(
                    item.SaleId, saleNumber, item.Id, item.ProductNameSnapshot, item.VariantNameSnapshot,
                    takeNow, FulfillmentMethod.TakeNow, FulfillmentStatus.Completed,
                    ScheduledDate: null, CompletedAtUtc: null, RecipientName: null, SourceScheduleId: null, ReplacementScheduleId: null));
                totalTakeNow += takeNow;
            }

            if (deliveryUnscheduled > 0m)
            {
                rows.Add(new FulfillmentReportRowDto(
                    item.SaleId, saleNumber, item.Id, item.ProductNameSnapshot, item.VariantNameSnapshot,
                    deliveryUnscheduled, FulfillmentMethod.Delivery, FulfillmentStatus.Unscheduled,
                    ScheduledDate: null, CompletedAtUtc: null, RecipientName: null, SourceScheduleId: null, ReplacementScheduleId: null));
                totalDeliveryUnscheduled += deliveryUnscheduled;
            }

            if (pickupUnscheduled > 0m)
            {
                rows.Add(new FulfillmentReportRowDto(
                    item.SaleId, saleNumber, item.Id, item.ProductNameSnapshot, item.VariantNameSnapshot,
                    pickupUnscheduled, FulfillmentMethod.Pickup, FulfillmentStatus.Unscheduled,
                    ScheduledDate: null, CompletedAtUtc: null, RecipientName: null, SourceScheduleId: null, ReplacementScheduleId: null));
                totalPickupUnscheduled += pickupUnscheduled;
            }

            foreach (var sched in scheduleForItem)
            {
                rows.Add(new FulfillmentReportRowDto(
                    item.SaleId, saleNumber, item.Id, item.ProductNameSnapshot, item.VariantNameSnapshot,
                    sched.Quantity, sched.Method, sched.Status, sched.ScheduledDate, sched.CompletedAtUtc, sched.RecipientName,
                    SourceScheduleId: sched.ScheduleId, ReplacementScheduleId: null));
            }

            // Cancellation history — its own rows, deliberately NOT folded into any total* accumulator
            // below (a cancelled schedule's quantity must never count toward a summary total; see the
            // query comment above). ReplacementScheduleId is populated when this cancellation produced a
            // new schedule (ConvertToPickup / ConvertToDelivery / CustomerPickedUpInstead); null for a
            // plain release (DeliverLater / PickupLater), which created no replacement.
            foreach (var cancelled in cancelledScheduleRowsByItem[item.Id])
            {
                replacementBySource.TryGetValue(cancelled.ScheduleId, out var replacementScheduleId);
                rows.Add(new FulfillmentReportRowDto(
                    item.SaleId, saleNumber, item.Id, item.ProductNameSnapshot, item.VariantNameSnapshot,
                    cancelled.Quantity, cancelled.Method, cancelled.Status, cancelled.ScheduledDate, cancelled.CompletedAtUtc, cancelled.RecipientName,
                    SourceScheduleId: cancelled.ScheduleId, ReplacementScheduleId: replacementScheduleId));
            }

            totalDeliveryPending += deliveryPending;
            totalDelivered += deliveryCompleted;
            totalPickupPending += pickupPending;
            totalClaimed += pickupCompleted;

            var overdueForItem = scheduleForItem.Any(r => r.Status == FulfillmentStatus.Pending && r.ScheduledDate < todayLocal);

            perSale.TryGetValue(item.SaleId, out var acc);
            perSale[item.SaleId] = (
                acc.Sold + item.Quantity,
                acc.TakeNow + takeNow,
                acc.Delivered + deliveryCompleted,
                acc.Claimed + pickupCompleted,
                acc.DeliveryPending + deliveryPending,
                acc.PickupPending + pickupPending,
                acc.DeliveryUnscheduled + deliveryUnscheduled,
                acc.PickupUnscheduled + pickupUnscheduled,
                acc.Overdue || overdueForItem);
        }

        IEnumerable<FulfillmentReportRowDto> filteredRows = rows;
        if (query.Method is { } methodFilter)
        {
            filteredRows = filteredRows.Where(r => r.Method == methodFilter);
        }

        if (query.Status is { } statusFilter)
        {
            filteredRows = filteredRows.Where(r => r.Status == statusFilter);
        }

        var ordered = filteredRows
            .OrderBy(r => r.SaleNumber)
            .ThenBy(r => r.SaleItemId)
            .ThenBy(r => r.Method)
            .ThenBy(r => r.Status)
            .ToList();

        var (page, pageSize) = PagedResult<FulfillmentReportRowDto>.Normalize(query.Page, query.PageSize);
        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var totalPages = ordered.Count == 0 ? 0 : (int)Math.Ceiling(ordered.Count / (double)pageSize);

        var fullyFulfilledSales = 0;
        var salesNeedingAttention = 0;
        foreach (var acc in perSale.Values)
        {
            var status = SaleFulfillmentCalculator.Derive(
                soldQuantity: acc.Sold,
                takeNowQuantity: acc.TakeNow,
                deliveredQuantity: acc.Delivered,
                claimedQuantity: acc.Claimed,
                deliveryPendingQuantity: acc.DeliveryPending,
                pickupPendingQuantity: acc.PickupPending,
                deliveryUnscheduledQuantity: acc.DeliveryUnscheduled,
                pickupUnscheduledQuantity: acc.PickupUnscheduled,
                hasOverduePendingSchedule: acc.Overdue);

            if (status == SaleFulfillmentStatus.Fulfilled) fullyFulfilledSales++;
            if (status == SaleFulfillmentStatus.NeedsAttention) salesNeedingAttention++;
        }

        // See this method's doc comment: summed over `sales` (one row per sale, never joined to a
        // schedule), so this can never be multiplied by however many schedules a sale has.
        var totalDeliveryCharges = sales.Sum(s => s.DeliveryCharge);

        var summary = new FulfillmentReportSummaryDto(
            totalTakeNow, totalDeliveryUnscheduled, totalDeliveryPending, totalDelivered,
            totalPickupUnscheduled, totalPickupPending, totalClaimed, cancelledSchedulesCount,
            fullyFulfilledSales, salesNeedingAttention, totalDeliveryCharges);

        return new FulfillmentReportResultDto(
            new PagedResult<FulfillmentReportRowDto>(pageItems, page, pageSize, ordered.Count, totalPages),
            summary);
    }

    public async Task<DeliveryFulfillmentReportResultDto> GetDeliveryFulfillmentAsync(
        DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow + ReportPeriodResolver.BusinessOffset);

        var qualifyingSales = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status != SaleStatus.Voided
            && _db.SaleItems.Any(i => i.SaleId == s.Id && i.DeliveryRequiredQuantity > 0m));
        if (branchFilter is { } b)
        {
            qualifyingSales = qualifyingSales.Where(s => s.BranchId == b);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            qualifyingSales = qualifyingSales.Where(s => s.SaleNumber.Contains(term));
        }

        // Aggregated in SQL, but the FulfillmentStatus derivation itself is arithmetic-only and cannot
        // be translated into a single EF-queryable predicate — so it's computed in memory over this
        // already tenant/branch/search-narrowed (delivery-bearing sales only) candidate set, never the
        // whole Sales table. Acceptable for this report's realistic scale; revisit if a tenant's
        // delivery-bearing sale count ever grows large enough for this to matter.
        var rows = await qualifyingSales
            .Select(s => new
            {
                s.Id,
                s.SaleNumber,
                s.CreatedAtUtc,
                s.DeliveryCharge,
                TotalRequired = _db.SaleItems.Where(i => i.SaleId == s.Id).Sum(i => i.DeliveryRequiredQuantity),
                // Every DeliveryReceipts predicate below is Method-filtered: the table is now shared with
                // pickups, and this report is the delivery-only view.
                TotalPending = _db.DeliveryReceiptItems.Where(dri => _db.DeliveryReceipts
                        .Any(d => d.Id == dri.DeliveryReceiptId && d.SaleId == s.Id
                            && d.Method == FulfillmentMethod.Delivery && d.Status == FulfillmentStatus.Pending))
                    .Sum(dri => (decimal?)dri.Quantity) ?? 0m,
                TotalDelivered = _db.DeliveryReceiptItems.Where(dri => _db.DeliveryReceipts
                        .Any(d => d.Id == dri.DeliveryReceiptId && d.SaleId == s.Id
                            && d.Method == FulfillmentMethod.Delivery && d.Status == FulfillmentStatus.Completed))
                    .Sum(dri => (decimal?)dri.Quantity) ?? 0m,
                ScheduleCount = _db.DeliveryReceipts.Count(d => d.SaleId == s.Id && d.Method == FulfillmentMethod.Delivery),
                HasOverduePending = _db.DeliveryReceipts.Any(d => d.SaleId == s.Id && d.Method == FulfillmentMethod.Delivery
                    && d.Status == FulfillmentStatus.Pending && d.ScheduledDate < todayLocal),
            })
            .ToListAsync(cancellationToken);

        var mapped = rows.Select(r =>
        {
            var available = r.TotalRequired - r.TotalPending - r.TotalDelivered;
            // Delivery-only projection onto the now method-aware calculator: this view's "sold" quantity
            // IS the delivery-required quantity (take-now is out of scope here) and every pickup bucket is
            // zero. The pickup and combined views are a separate task.
            var status = SaleFulfillmentCalculator.Derive(
                soldQuantity: r.TotalRequired,
                takeNowQuantity: 0m,
                deliveredQuantity: r.TotalDelivered,
                claimedQuantity: 0m,
                deliveryPendingQuantity: r.TotalPending,
                pickupPendingQuantity: 0m,
                deliveryUnscheduledQuantity: available,
                pickupUnscheduledQuantity: 0m,
                hasOverduePendingSchedule: r.HasOverduePending);
            return new DeliveryFulfillmentReportRowDto(
                r.Id, r.SaleNumber, r.CreatedAtUtc, status, r.DeliveryCharge,
                r.TotalRequired, r.TotalPending, r.TotalDelivered, available, r.ScheduleCount);
        });

        if (query.Status is { } statusFilter)
        {
            mapped = mapped.Where(m => m.FulfillmentStatus == statusFilter);
        }

        var ordered = mapped
            // NeedsAttention replaces the old NeedsRescheduling member — same meaning (something is
            // overdue), same "float it to the top" intent.
            .OrderBy(m => m.FulfillmentStatus == SaleFulfillmentStatus.NeedsAttention ? 0 : 1)
            .ThenByDescending(m => m.SaleCreatedAtUtc)
            .ToList();

        var totals = new DeliveryFulfillmentReportTotalsDto(ordered.Count, ordered.Sum(m => m.DeliveryCharge));

        var pageSize = query.PageSize <= 0 ? PagedResult<DeliveryFulfillmentReportRowDto>.DefaultPageSize : query.PageSize;
        var page = Math.Max(query.Page, 1);
        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var totalPages = ordered.Count == 0 ? 0 : (int)Math.Ceiling(ordered.Count / (double)pageSize);

        return new DeliveryFulfillmentReportResultDto(
            new PagedResult<DeliveryFulfillmentReportRowDto>(pageItems, page, pageSize, ordered.Count, totalPages),
            totals);
    }

    // ---- shared filtering ----

    private async Task<IQueryable<Sale>> BaseSalesAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var tenantId = _currentUser.TenantId;
        // Branch-scoped roles (Manager) get their assigned branch forced regardless of what was
        // requested — the same enforcement SaleQueryService/DashboardService already rely on.
        var branchFilter = await _branchAccess.ResolveListFilterAsync(filter.BranchId, cancellationToken);

        var q = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (branchFilter is { } b)
        {
            q = q.Where(s => s.BranchId == b);
        }

        if (filter.RegisterId is { } registerId)
        {
            q = q.Where(s => _db.RegisterSessions.Any(rs => rs.Id == s.RegisterSessionId && rs.RegisterId == registerId));
        }

        if (filter.CashierId is { } cashierId)
        {
            q = q.Where(s => s.CreatedByUserId == cashierId);
        }

        return q;
    }

    private async Task<IQueryable<SaleReturn>> BaseReturnsAsync(ReportFilter filter, CancellationToken cancellationToken)
    {
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(filter.BranchId, cancellationToken);

        var q = _db.SaleReturns.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (branchFilter is { } b)
        {
            q = q.Where(r => r.BranchId == b);
        }

        if (filter.CashierId is { } cashierId)
        {
            q = q.Where(r => r.CreatedByUserId == cashierId);
        }

        // A return has no register of its own — it inherits the original sale's register.
        if (filter.RegisterId is { } registerId)
        {
            q = q.Where(r => _db.Sales.Any(s => s.Id == r.SaleId
                && _db.RegisterSessions.Any(rs => rs.Id == s.RegisterSessionId && rs.RegisterId == registerId)));
        }

        return q;
    }

    private static IQueryable<Sale> Qualifying(IQueryable<Sale> salesBase, DateTime fromUtc, DateTime toUtc) =>
        salesBase.Where(s => s.CreatedAtUtc >= fromUtc && s.CreatedAtUtc < toUtc && s.Status != SaleStatus.Voided);

    // ---- KPIs ----

    /// <summary>
    /// A handful of small, separately-awaited aggregate queries rather than one combined query —
    /// each is a trivial indexed SUM/COUNT, and keeping them separate avoids the empty-result-set
    /// ambiguity a single GroupBy(_ => 1) trick would introduce (a GroupBy over zero rows produces
    /// zero groups, not one group of zeros). Clarity over shaving a few round trips on an admin page.
    /// </summary>
    private async Task<ReportKpiDto> ComputeKpisAsync(
        IQueryable<Sale> salesBase, IQueryable<SaleReturn> returnsBase, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var qualifying = Qualifying(salesBase, fromUtc, toUtc);

        var gross = await qualifying.SumAsync(s => s.Subtotal, cancellationToken);
        var discounts = await qualifying.SumAsync(s => s.DiscountTotal, cancellationToken);
        var tax = await qualifying.SumAsync(s => s.TaxTotal, cancellationToken);
        var net = await qualifying.SumAsync(s => s.GrandTotal, cancellationToken);
        var count = await qualifying.CountAsync(cancellationToken);

        var itemsSold = await (from i in _db.SaleItems.AsNoTracking()
                                join s in qualifying on i.SaleId equals s.Id
                                select i.Quantity).SumAsync(cancellationToken);

        var voidedQuery = salesBase.Where(s => s.Status == SaleStatus.Voided
            && s.VoidedAtUtc != null && s.VoidedAtUtc >= fromUtc && s.VoidedAtUtc < toUtc);
        var voidedCount = await voidedQuery.CountAsync(cancellationToken);
        var voidedValue = await voidedQuery.SumAsync(s => s.GrandTotal, cancellationToken);

        var returnsQuery = returnsBase.Where(r => r.CreatedAtUtc >= fromUtc && r.CreatedAtUtc < toUtc);
        var returnsCount = await returnsQuery.CountAsync(cancellationToken);
        var returnsValue = await returnsQuery.SumAsync(r => r.TotalRefund, cancellationToken);

        var netCollected = net - returnsValue;
        var averageTransactionValue = count == 0 ? 0m : Money.Round(net / count);

        return new ReportKpiDto(
            gross, discounts, tax, net, returnsValue, netCollected, count, averageTransactionValue, itemsSold,
            voidedCount, voidedValue, returnsCount);
    }

    private static decimal? PercentChange(decimal previous, decimal current)
    {
        if (previous == 0m)
        {
            // Nothing to compare against — 0% only if current is also 0; otherwise there is no
            // meaningful percentage (never fabricate one, e.g. "infinite%" or a made-up number).
            return current == 0m ? 0m : null;
        }

        return Math.Round((current - previous) / previous * 100m, 1, MidpointRounding.AwayFromZero);
    }

    private static decimal? PercentChange(int previous, int current) => PercentChange((decimal)previous, current);

    // ---- trend ----

    private async Task<IReadOnlyList<SalesTrendPointDto>> ComputeTrendAsync(
        IQueryable<Sale> salesBase, ResolvedReportRange range, CancellationToken cancellationToken)
    {
        var qualifying = Qualifying(salesBase, range.FromUtc, range.ToUtc);

        if (range.Hourly)
        {
            var rows = await qualifying
                .Select(s => new { Hour = s.CreatedAtUtc.AddHours(8).Hour, s.GrandTotal })
                .GroupBy(x => x.Hour)
                .Select(g => new { Hour = g.Key, Net = g.Sum(x => x.GrandTotal), Count = g.Count() })
                .ToListAsync(cancellationToken);
            var byHour = rows.ToDictionary(r => r.Hour);

            var points = new List<SalesTrendPointDto>(24);
            for (var hour = 0; hour < 24; hour++)
            {
                var bucketStartUtc = range.FromUtc.AddHours(hour);
                var label = FormatHourLabel(hour);
                points.Add(byHour.TryGetValue(hour, out var row)
                    ? new SalesTrendPointDto(label, bucketStartUtc, row.Net, row.Count)
                    : new SalesTrendPointDto(label, bucketStartUtc, 0m, 0));
            }

            return points;
        }

        var dailyRows = await qualifying
            .Select(s => new { LocalDate = s.CreatedAtUtc.AddHours(8).Date, s.GrandTotal })
            .GroupBy(x => x.LocalDate)
            .Select(g => new { Date = g.Key, Net = g.Sum(x => x.GrandTotal), Count = g.Count() })
            .ToListAsync(cancellationToken);
        var byDate = dailyRows.ToDictionary(r => r.Date);

        var totalDays = (int)Math.Round((range.ToUtc - range.FromUtc).TotalDays);
        var dailyPoints = new List<SalesTrendPointDto>(totalDays);
        for (var day = 0; day < totalDays; day++)
        {
            var bucketStartUtc = range.FromUtc.AddDays(day);
            var localDate = (bucketStartUtc + ReportPeriodResolver.BusinessOffset).Date;
            var label = localDate.ToString("MMM d");
            dailyPoints.Add(byDate.TryGetValue(localDate, out var row)
                ? new SalesTrendPointDto(label, bucketStartUtc, row.Net, row.Count)
                : new SalesTrendPointDto(label, bucketStartUtc, 0m, 0));
        }

        return dailyPoints;
    }

    private static string FormatHourLabel(int hour) => DateTime.MinValue.AddHours(hour).ToString("h tt");

    // ---- payment methods ----

    private async Task<IReadOnlyList<PaymentMethodBreakdownDto>> ComputePaymentMethodsAsync(
        IQueryable<Sale> salesBase, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
    {
        var qualifying = Qualifying(salesBase, fromUtc, toUtc);

        // Grouped by payment RECORD, not by sale — a split-tender sale correctly contributes to more
        // than one method's amount/count here.
        var rows = await (from p in _db.Payments.AsNoTracking()
                           join s in qualifying on p.SaleId equals s.Id
                           group p by p.Method into g
                           select new { Method = g.Key, Amount = g.Sum(x => x.Amount), Count = g.Count() })
            .ToListAsync(cancellationToken);

        var total = rows.Sum(r => r.Amount);

        return rows
            .OrderByDescending(r => r.Amount)
            .Select(r => new PaymentMethodBreakdownDto(
                r.Method, r.Amount, r.Count,
                total == 0m ? 0m : Math.Round(r.Amount / total * 100m, 1, MidpointRounding.AwayFromZero)))
            .ToList();
    }

    private void RequireAuthenticated()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }
    }
}
