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

// ---- Pickup report (mirrors the delivery report above, Method == Pickup) ----

/// <summary>Same shape as <see cref="DeliveryReportPreset"/> with <c>Claimed</c> standing in for
/// <c>Delivered</c> — a pickup is "claimed", never "delivered". <c>Pending</c> is an explicit preset
/// here (the delivery report has no equivalent standalone member — Overdue already covers its own
/// "still open" case) because the spec names it directly for pickup.</summary>
public enum PickupReportPreset
{
    All = 1,
    Today = 2,
    Upcoming = 3,
    Overdue = 4,
    Pending = 5,
    Claimed = 6,
    Cancelled = 7,
}

/// <summary>When <see cref="Preset"/> is anything but <see cref="PickupReportPreset.All"/>, it resolves
/// to a concrete date range / status / overdue-only filter server-side and <see cref="FromDate"/>,
/// <see cref="ToDate"/>, <see cref="Status"/> are ignored — same contract as <see cref="DeliveryReportQuery"/>.</summary>
public sealed record PickupReportQuery(
    Guid? BranchId = null,
    PickupReportPreset Preset = PickupReportPreset.All,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    FulfillmentStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<PickupReportRowDto>.DefaultPageSize);

/// <summary>One pickup schedule row. No address column (a pickup transports nothing) and
/// <see cref="DeliveryCharge"/> is always 0 — a pickup never carries one, regardless of what the
/// underlying Sale's own DeliveryCharge is (that charge, if any, belongs to the sale's delivery side).
/// <see cref="SaleGrandTotal"/> is read live from the linked Sale, same as the delivery report.</summary>
public sealed record PickupReportRowDto(
    Guid DeliveryReceiptId,
    Guid SaleId,
    string SaleNumber,
    int SequenceNumber,
    DateOnly ScheduledPickupDate,
    FulfillmentStatus Status,
    bool IsOverdue,
    DateTime CreatedAtUtc,
    string RecipientName,
    string? ContactNumber,
    string? Notes,
    decimal DeliveryCharge,
    decimal SaleGrandTotal,
    string PaymentSummary,
    string PreparedByName,
    DateTime? CompletedAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason);

/// <summary>Deliberately no charge-related figures here (unlike <see cref="DeliveryReportTotalsDto"/>) —
/// a pickup never carries a delivery charge, so a charge total would always read zero and add nothing.</summary>
public sealed record PickupReportTotalsDto(int TotalSchedules, int DistinctSalesCount);

public sealed record PickupReportResultDto(PagedResult<PickupReportRowDto> Page, PickupReportTotalsDto Totals);

// ---- Combined fulfillment view (one row per sale-item-per-method allocation) ----

/// <summary>Optional row-level narrowing on top of the same branch/search scoping the other two report
/// endpoints use. <see cref="Method"/>/<see cref="Status"/> filter the ALLOCATION rows themselves (e.g.
/// "show only pending pickups"), not which sales qualify — narrowing happens after allocation rows are
/// built, so it can select a status/method combination that appears on some but not all of a sale's
/// lines without hiding the rest of that sale's other rows.</summary>
public sealed record FulfillmentReportQuery(
    Guid? BranchId = null,
    FulfillmentMethod? Method = null,
    FulfillmentStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<FulfillmentReportRowDto>.DefaultPageSize);

/// <summary>
/// One ALLOCATION — a specific quantity of one sale item sitting in one method/status bucket, never a
/// whole sale. A sale item split across take-now, a completed delivery, a still-pending delivery and an
/// unscheduled pickup produces four of these. <see cref="Status"/> is the only place in the codebase
/// where <see cref="FulfillmentStatus.Unscheduled"/> legitimately appears — synthesized for intent that
/// has no matching schedule yet, never a stored row.
/// <para>A cancelled schedule IS its own row (<see cref="Status"/> = <see cref="FulfillmentStatus.Cancelled"/>),
/// carrying its cancelled quantity, its own id as <see cref="SourceScheduleId"/>, and — when the
/// cancellation created one — the schedule it became as <see cref="ReplacementScheduleId"/> (looked up
/// from <see cref="Negosio.Domain.Entities.FulfillmentConversion.SourceRecordId"/>/
/// <c>ReplacementRecordId</c>; null for a plain release such as DeliverLater/PickupLater, which creates no
/// replacement). This is what the report's cancellation/conversion history requirement means: the cancelled
/// row is history, never re-added to any quantity total — the summary's seven quantity totals are computed
/// exclusively from non-cancelled schedules, exactly as if this row did not exist for that purpose.</para>
/// <para><see cref="ScheduledDate"/>/<see cref="CompletedAtUtc"/>/<see cref="RecipientName"/>/
/// <see cref="SourceScheduleId"/>/<see cref="ReplacementScheduleId"/> are all null for a synthesized
/// (TakeNow or Unscheduled) row — there is no schedule to read them from.</para>
/// </summary>
public sealed record FulfillmentReportRowDto(
    Guid SaleId,
    string SaleNumber,
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    FulfillmentMethod Method,
    FulfillmentStatus Status,
    DateOnly? ScheduledDate,
    DateTime? CompletedAtUtc,
    string? RecipientName,
    Guid? SourceScheduleId,
    Guid? ReplacementScheduleId);

/// <summary>
/// The ten totals the spec names, aggregated over the FULL filtered set (every qualifying sale, not just
/// the current page) — same "aggregate over everything, not the page" contract as
/// <see cref="DeliveryReportTotalsDto"/>. The first seven are quantity sums; <see cref="TotalCancelledSchedules"/>
/// counts schedule RECORDS (not units). <see cref="FullyFulfilledSalesCount"/> and
/// <see cref="SalesNeedingAttentionCount"/> are sale counts derived by running
/// <see cref="Negosio.Application.Delivery.SaleFulfillmentCalculator.Derive"/> once per qualifying sale —
/// never a separate ad-hoc computation — so this report and the Sale-detail page can never disagree about
/// a sale's status. <see cref="TotalDeliveryCharges"/> is not one of the spec's ten named totals but is
/// included for the same reason <see cref="DeliveryReportTotalsDto.TotalDeliveryCharges"/> exists: it is
/// summed over each qualifying sale's DeliveryCharge counted exactly ONCE (see
/// <c>ReportsService.GetFulfillmentAsync</c>'s doc comment for why this can never be multiplied by however
/// many delivery/pickup schedules that sale has).
/// </summary>
public sealed record FulfillmentReportSummaryDto(
    decimal TotalTakeNowQuantity,
    decimal TotalDeliveryUnscheduledQuantity,
    decimal TotalDeliveryPendingQuantity,
    decimal TotalDeliveredQuantity,
    decimal TotalPickupUnscheduledQuantity,
    decimal TotalPickupPendingQuantity,
    decimal TotalClaimedQuantity,
    int TotalCancelledSchedules,
    int FullyFulfilledSalesCount,
    int SalesNeedingAttentionCount,
    decimal TotalDeliveryCharges);

public sealed record FulfillmentReportResultDto(
    PagedResult<FulfillmentReportRowDto> Page, FulfillmentReportSummaryDto Summary);

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

    Task<PickupReportResultDto> GetPickupsAsync(PickupReportQuery query, CancellationToken cancellationToken = default);

    Task<FulfillmentReportResultDto> GetFulfillmentAsync(FulfillmentReportQuery query, CancellationToken cancellationToken = default);
}
