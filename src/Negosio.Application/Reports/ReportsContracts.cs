using Negosio.Application.Common;
using Negosio.Application.Delivery;
using Negosio.Domain.Enums;

namespace Negosio.Application.Reports;

/// <summary>Preset date ranges for a report request. Boundaries are always resolved server-side
/// (<see cref="IReportPeriodResolver"/>) — never trusted from the browser's own clock/timezone.</summary>
public enum ReportPeriod
{
    Today = 1,
    Yesterday = 2,
    Last7Days = 3,
    Last30Days = 4,
    ThisMonth = 5,
    Custom = 6,
}

/// <summary>
/// One shared filter shape for every report endpoint. <see cref="FromDate"/>/<see cref="ToDate"/>
/// are only required (and only used) when <see cref="Period"/> is <see cref="ReportPeriod.Custom"/> —
/// every other preset is resolved from the current business date, ignoring these.
/// </summary>
public sealed record ReportFilter(
    ReportPeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    Guid? BranchId,
    Guid? RegisterId,
    Guid? CashierId);

public sealed record ResolvedReportRange(
    DateTime FromUtc, DateTime ToUtc, DateTime PreviousFromUtc, DateTime PreviousToUtc, bool Hourly);

public interface IReportPeriodResolver
{
    ResolvedReportRange Resolve(ReportPeriod period, DateOnly? fromDate, DateOnly? toDate);
}

/// <summary>
/// Core sales KPIs for a range. See docs/reports-formulas or ReportsService's XML doc for the exact
/// derivation — every figure here is grounded in a real persisted field, never invented.
/// </summary>
public sealed record ReportKpiDto(
    decimal GrossSales,
    decimal Discounts,
    decimal Tax,
    decimal NetSales,
    decimal Returns,
    decimal NetCollected,
    int CompletedTransactions,
    decimal AverageTransactionValue,
    decimal TotalItemsSold,
    int VoidedSalesCount,
    decimal VoidedSalesValue,
    int ReturnsCount);

/// <summary>
/// Percent change vs. the immediately preceding period of equal length. Null — never a fabricated
/// number — when the previous period's figure was zero and there is nothing meaningful to divide by.
/// </summary>
public sealed record ReportKpiComparisonDto(
    decimal? GrossSalesChangePercent,
    decimal? NetSalesChangePercent,
    decimal? TransactionsChangePercent,
    decimal? AverageTransactionChangePercent,
    decimal? ReturnsChangePercent);

/// <summary>One bucket of the sales trend chart. <see cref="BucketLabel"/> is pre-formatted
/// server-side in business-local time ("9 AM", "Sep 9") — the frontend renders it verbatim and never
/// re-parses/re-converts it, so it can't accidentally reinterpret it in the viewer's own timezone.</summary>
public sealed record SalesTrendPointDto(string BucketLabel, DateTime BucketStartUtc, decimal NetSales, int Transactions);

/// <summary>One payment method's slice. <see cref="PaymentCount"/> counts payment *records*, not
/// sales — a split-tender sale contributes to more than one method's count/amount, by design.</summary>
public sealed record PaymentMethodBreakdownDto(PaymentMethod Method, decimal Amount, int PaymentCount, decimal Percentage);

public sealed record ReportsOverviewDto(
    DateTime FromUtc,
    DateTime ToUtc,
    ReportKpiDto Kpis,
    ReportKpiComparisonDto Comparison,
    bool TrendIsHourly,
    IReadOnlyList<SalesTrendPointDto> Trend,
    IReadOnlyList<PaymentMethodBreakdownDto> PaymentMethods);

/// <summary>Grouped by the stable <see cref="ProductVariantId"/>, not by name — a later rename can't
/// split one product's sales into two rows. Product/variant/SKU shown here are the *current* catalog
/// values (a live join), not a point-in-time snapshot — same documented limitation as
/// <see cref="CategoryPerformanceDto"/>.</summary>
public sealed record TopProductDto(
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    string? Sku,
    string? CategoryName,
    decimal QuantitySold,
    decimal SalesAmount);

/// <summary>
/// Grouped by the product's *current* <see cref="Negosio.Domain.Entities.Product.CategoryId"/> — a
/// product recategorized after the sale reports under its new category. There is no per-sale category
/// snapshot in the domain to do otherwise; acceptable for v1, documented rather than silently wrong.
/// </summary>
public sealed record CategoryPerformanceDto(
    Guid? CategoryId,
    string CategoryName,
    decimal QuantitySold,
    decimal SalesAmount,
    decimal PercentageOfSales);

public enum DeliveryReportPreset
{
    All = 1,
    Today = 2,
    Upcoming = 3,
    Overdue = 4,
    Delivered = 5,
    Cancelled = 6,
    NeedsRescheduling = 7,
}

/// <summary>
/// When <see cref="Preset"/> is anything but <see cref="DeliveryReportPreset.All"/>, it resolves to a
/// concrete date range / status / overdue-only filter server-side and <see cref="FromDate"/>,
/// <see cref="ToDate"/>, <see cref="Status"/> are ignored — pass either a preset OR explicit filters,
/// not both.
/// </summary>
public sealed record DeliveryReportQuery(
    Guid? BranchId = null,
    DeliveryReportPreset Preset = DeliveryReportPreset.All,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    FulfillmentStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<DeliveryReportRowDto>.DefaultPageSize);

/// <summary>One delivery schedule row. <see cref="DeliveryCharge"/> and <see cref="SaleGrandTotal"/>
/// are read live from the linked Sale — never a per-schedule copy (see the plan's Global
/// Constraints) — so the SAME sale's charge appears identically on every one of its schedule rows;
/// <see cref="DeliveryReportTotalsDto"/> is what avoids double-counting it in the summary.</summary>
public sealed record DeliveryReportRowDto(
    Guid DeliveryReceiptId,
    Guid SaleId,
    string SaleNumber,
    int SequenceNumber,
    DateOnly ScheduledDeliveryDate,
    FulfillmentStatus Status,
    bool IsOverdue,
    DateTime CreatedAtUtc,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    decimal DeliveryCharge,
    decimal SaleGrandTotal,
    string PaymentSummary,
    string PreparedByName,
    DateTime? DeliveredAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason);

/// <summary>Aggregates over the FULL filtered set, not just the current page. <see cref="TotalSchedules"/>
/// counts DeliveryReceipt rows (how many schedules matched); every charge-related figure is computed
/// over the filtered set's DISTINCT sales instead — the fix for the bug this task addresses: summing
/// DeliveryCharge per DR row double/triple-counts a sale with more than one schedule.</summary>
public sealed record DeliveryReportTotalsDto(
    int TotalSchedules,
    int DistinctSalesCount,
    int FreeDeliverySalesCount,
    int ChargedDeliverySalesCount,
    decimal TotalDeliveryCharges,
    decimal AverageDeliveryChargePerSale);

public sealed record DeliveryReportResultDto(PagedResult<DeliveryReportRowDto> Page, DeliveryReportTotalsDto Totals);

// ---- Sale fulfillment view ----

public sealed record DeliveryFulfillmentReportQuery(
    Guid? BranchId = null,
    SaleFulfillmentStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<DeliveryFulfillmentReportRowDto>.DefaultPageSize);

public sealed record DeliveryFulfillmentReportRowDto(
    Guid SaleId,
    string SaleNumber,
    DateTime SaleCreatedAtUtc,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    decimal TotalDeliveryRequiredQuantity,
    decimal TotalPendingQuantity,
    decimal TotalDeliveredQuantity,
    decimal TotalUnscheduledQuantity,
    int ScheduleCount);

public sealed record DeliveryFulfillmentReportTotalsDto(int TotalSales, decimal TotalDeliveryCharges);

public sealed record DeliveryFulfillmentReportResultDto(
    PagedResult<DeliveryFulfillmentReportRowDto> Page, DeliveryFulfillmentReportTotalsDto Totals);

public interface IReportsService
{
    Task<ReportsOverviewDto> GetOverviewAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TopProductDto>> GetTopProductsAsync(ReportFilter filter, int top, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryPerformanceDto>> GetCategoryPerformanceAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<DeliveryReportResultDto> GetDeliveriesAsync(DeliveryReportQuery query, CancellationToken cancellationToken = default);

    Task<DeliveryFulfillmentReportResultDto> GetDeliveryFulfillmentAsync(DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken = default);
}
