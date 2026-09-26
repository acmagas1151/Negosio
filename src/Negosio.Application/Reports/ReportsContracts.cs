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
/// <see cref="DeliveryReportTotalsDto"/> is what avoids double-counting it in the summary.
/// <see cref="CancellationDisposition"/> is null for every non-cancelled row — the spec's "cancellation
/// reason and disposition" requirement, alongside <see cref="CancellationReason"/>.</summary>
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
    string? CancellationReason,
    CancellationDisposition? CancellationDisposition);

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
/// <see cref="SaleGrandTotal"/> is read live from the linked Sale, same as the delivery report.
/// <see cref="CancellationDisposition"/> is null for every non-cancelled row — the spec's "cancellation
/// reason and disposition" requirement, alongside <see cref="CancellationReason"/>.</summary>
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
    string? CancellationReason,
    CancellationDisposition? CancellationDisposition);

/// <summary>Deliberately no charge-related figures here (unlike <see cref="DeliveryReportTotalsDto"/>) —
/// a pickup never carries a delivery charge, so a charge total would always read zero and add nothing.</summary>
public sealed record PickupReportTotalsDto(int TotalSchedules, int DistinctSalesCount);

public sealed record PickupReportResultDto(PagedResult<PickupReportRowDto> Page, PickupReportTotalsDto Totals);

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

// ---- Branch performance ----

/// <summary>One branch's KPI slice for the resolved range — same formulas as <see cref="ReportKpiDto"/>
/// (see <c>ReportsService</c>'s own doc comment for the exact derivations), grouped by
/// <see cref="BranchId"/> instead of aggregated tenant-wide. Voided sales are never folded into
/// <see cref="GrossSales"/>/<see cref="NetSales"/> — they get their own
/// <see cref="VoidedSalesCount"/>/<see cref="VoidedSalesValue"/>, same rule as the overview report.</summary>
public sealed record BranchPerformanceRowDto(
    Guid BranchId,
    string BranchName,
    decimal GrossSales,
    decimal NetSales,
    int CompletedTransactions,
    decimal AverageTransactionValue,
    decimal Discounts,
    int ReturnsCount,
    decimal ReturnsValue,
    int VoidedSalesCount,
    decimal VoidedSalesValue);

public sealed record BranchPerformanceResultDto(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<BranchPerformanceRowDto> Rows);

// ---- Register performance ----

/// <summary>One register's KPI slice for the resolved range, grouped by <see cref="RegisterId"/> (a
/// register belongs to exactly one branch, so this still needs the same branch-access scoping as
/// <see cref="BranchPerformanceRowDto"/> — see <c>ReportsService.GetRegisterPerformanceAsync</c>).
/// <see cref="PaymentMethods"/> follows the same split-tender-safe, per-payment-record convention as
/// the overview report's own breakdown — a split-tender sale contributes once per method, never once
/// per sale. <see cref="CashIn"/>/<see cref="CashOut"/> are totals of
/// <see cref="Negosio.Domain.Entities.RegisterCashMovement.Amount"/> for that register's
/// <see cref="Negosio.Domain.Enums.CashMovementType.CashIn"/>/<see cref="Negosio.Domain.Enums.CashMovementType.CashOut"/>
/// movements — always positive, summed separately, never subtracted (Amount itself carries no sign).</summary>
public sealed record RegisterPerformanceRowDto(
    Guid RegisterId,
    string RegisterName,
    Guid BranchId,
    string BranchName,
    decimal GrossSales,
    decimal NetSales,
    int CompletedTransactions,
    decimal AverageTransactionValue,
    IReadOnlyList<PaymentMethodBreakdownDto> PaymentMethods,
    decimal CashIn,
    decimal CashOut);

public sealed record RegisterPerformanceResultDto(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<RegisterPerformanceRowDto> Rows);

// ---- Register session reconciliation (session-granular, closed sessions only) ----

/// <summary>Filter/paging shape for the closed-register-session reconciliation list. Deliberately its
/// own query record rather than reusing <see cref="ReportFilter"/> — this report has no
/// <c>CashierId</c> filter (a session's reconciliation figures belong to whoever closed it, not a
/// single cashier) and is paged, unlike every <see cref="ReportFilter"/>-based report.</summary>
public sealed record RegisterSessionReconciliationQuery(
    ReportPeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    Guid? BranchId,
    Guid? RegisterId,
    int Page = 1,
    int PageSize = PagedResult<RegisterSessionReconciliationRowDto>.DefaultPageSize);

/// <summary>One CLOSED <see cref="Negosio.Domain.Entities.RegisterSession"/>, verbatim — every cash
/// figure here is read directly off the session row exactly as <c>RegisterSessionService.ReconcileAndCloseAsync</c>
/// computed and persisted it once, at close time. Never recomputed, never proportionally split across
/// a date range: a session that stayed open for days, or crossed midnight, still contributes exactly
/// one row here, dated by <see cref="ClosedAtUtc"/>. An <c>Open</c> session never appears — it has none
/// of these figures yet (see <c>ReportsService.GetRegisterSessionReconciliationAsync</c>).</summary>
public sealed record RegisterSessionReconciliationRowDto(
    Guid SessionId,
    Guid RegisterId,
    string RegisterName,
    Guid BranchId,
    string BranchName,
    DateTime OpenedAtUtc,
    DateTime ClosedAtUtc,
    string OpenedByName,
    string ClosedByName,
    decimal OpeningCash,
    decimal ClosingCash,
    decimal ExpectedCash,
    decimal CashDifference,
    decimal GrossCashSales,
    decimal VoidedCashSales,
    decimal RefundCashOut,
    decimal CashIn,
    decimal CashOut);

// ---- Cashier performance ----

/// <summary>One user's KPI slice for the resolved range, attributed by whoever actually performed each
/// action rather than by role. <see cref="CashierUserId"/> keys the sales-side figures
/// (<see cref="GrossSales"/>/<see cref="NetSales"/>/<see cref="CompletedTransactions"/>/
/// <see cref="AverageTransactionValue"/>/<see cref="Discounts"/>) to
/// <see cref="Negosio.Domain.Entities.Sale.CreatedByUserId"/> (who rang up the checkout), while
/// <see cref="VoidedSalesCount"/>/<see cref="VoidedSalesValue"/> are keyed to
/// <see cref="Negosio.Domain.Entities.Sale.VoidedByUserId"/> (who performed the void) and
/// <see cref="ReturnsCount"/>/<see cref="ReturnsValue"/> to
/// <see cref="Negosio.Domain.Entities.SaleReturn.CreatedByUserId"/> (who processed the return) — three
/// independently-keyed buckets that can land on three different people for the very same sale (e.g.
/// Cashier A rings it up, Manager B later voids it: A's row never counts that sale toward Net/Gross
/// once it's voided — same <c>Qualifying</c> rule as every other report here — while B's row picks up
/// the void). No role filter is applied anywhere in this query — a Manager or Owner who personally
/// completes a checkout, voids a sale, or processes a return shows up here exactly like a Cashier would,
/// by design (see <c>ReportsService.GetCashierPerformanceAsync</c>). <see cref="Discounts"/> is
/// amount/count only — this codebase does not persist who APPROVED a discount anywhere, so no approver
/// identity is exposed or implied here, only the total already carried on
/// <see cref="Negosio.Domain.Entities.Sale.DiscountTotal"/>. <see cref="VoidApprovalsCount"/>/
/// <see cref="ReturnApprovalsCount"/> are a THIRD, independently-keyed bucket — grouped by
/// <see cref="Negosio.Domain.Entities.Sale.ApprovedByUserId"/>/
/// <see cref="Negosio.Domain.Entities.SaleReturn.ApprovedByUserId"/> (the approver, null when the actor
/// acted under their own direct authority), never conflated with the actor's own
/// <see cref="VoidedSalesCount"/>/<see cref="ReturnsCount"/> — an approver who never personally voided or
/// returned anything still gets their own row via these counts. Count-only, not value: an approver isn't
/// financially "responsible" for the voided/refunded amount the way the actor is.</summary>
public sealed record CashierPerformanceRowDto(
    Guid CashierUserId,
    string CashierName,
    decimal GrossSales,
    decimal NetSales,
    int CompletedTransactions,
    decimal AverageTransactionValue,
    decimal Discounts,
    int ReturnsCount,
    decimal ReturnsValue,
    int VoidedSalesCount,
    decimal VoidedSalesValue,
    int VoidApprovalsCount,
    int ReturnApprovalsCount);

public sealed record CashierPerformanceResultDto(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<CashierPerformanceRowDto> Rows);

public interface IReportsService
{
    Task<ReportsOverviewDto> GetOverviewAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TopProductDto>> GetTopProductsAsync(ReportFilter filter, int top, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryPerformanceDto>> GetCategoryPerformanceAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<DeliveryReportResultDto> GetDeliveriesAsync(DeliveryReportQuery query, CancellationToken cancellationToken = default);

    Task<DeliveryFulfillmentReportResultDto> GetDeliveryFulfillmentAsync(DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken = default);

    Task<PickupReportResultDto> GetPickupsAsync(PickupReportQuery query, CancellationToken cancellationToken = default);

    Task<BranchPerformanceResultDto> GetBranchPerformanceAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<RegisterPerformanceResultDto> GetRegisterPerformanceAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<PagedResult<RegisterSessionReconciliationRowDto>> GetRegisterSessionReconciliationAsync(
        RegisterSessionReconciliationQuery query, CancellationToken cancellationToken = default);

    Task<CashierPerformanceResultDto> GetCashierPerformanceAsync(ReportFilter filter, CancellationToken cancellationToken = default);
}
