using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
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
///  - Net sales      = Σ Sale.GrandTotal           (after discount/tax, before returns)
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
