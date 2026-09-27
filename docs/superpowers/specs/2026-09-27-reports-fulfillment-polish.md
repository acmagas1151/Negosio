# Reports + Fulfillment Polish — Spec

_2026-09-27_

## Context

Phases #12–14 (whole-sale fulfillment simplification, Reports Phase C, fulfillment-cancel authorization fix) left four known, deliberately-deferred polish items, tracked in `negosio-status.md`. This spec scopes a single pass that closes all four.

## 1. Delivery report cleanup

`GetDeliveryFulfillmentAsync`'s row DTO (`DeliveryFulfillmentReportRowDto`, the "Sale fulfillment" sub-view under Reports → Fulfillment → Deliveries) still exposes `TotalDeliveryRequiredQuantity`/`TotalPendingQuantity`/`TotalDeliveredQuantity`/`TotalUnscheduledQuantity` — leftovers from the old per-item partial-allocation model. Under the current whole-sale model (one Sale, one method, 100% or 0% per item, at most one active schedule ever), these four columns can only ever show one of two shapes per sale: all-zero, or `Required == Pending` / `Required == Delivered` (schedule exists), or `Required` entirely unaccounted for (schedule never got created — the one genuine "needs attention" case, from a checkout that succeeded but whose schedule-creation call failed).

**Decision:** collapse the four quantity columns into one boolean, `NeedsScheduling` (`true` when a sale requires delivery but has no pending/completed schedule yet). Keep `FulfillmentStatus`, `DeliveryCharge`, `ScheduleCount`, `SaleCreatedAtUtc` as-is. Do not touch `SaleItem.DeliveryRequiredQuantity`/`PickupRequiredQuantity` themselves — they're still live inputs to checkout, schedule creation, and conversion elsewhere; only this one report DTO's four aggregate fields are stale.

Also remove `DeliveryReportPreset.NeedsRescheduling` — confirmed byte-for-byte identical to `Overdue` in `ResolveDeliveryPreset` (both resolve to `overdueOnly = true`, no other filter). Keep `PickupReportPreset` untouched (no duplicate found there).

Not in scope: the schedule-level Delivery/Pickup reports (`GetDeliveriesAsync`/`GetPickupsAsync`, the "Delivery schedule" and "Pickups" tabs) — these are already correct under the whole-sale model and untouched by this spec.

## 2. Performance drill-downs

Branch/Register/Cashier performance rows are pure aggregates with no click-through today. Add links from:
- Branch performance rows → `/sales` filtered by that branch + the report's resolved date range.
- Register performance ("Sales" sub-view) rows → `/sales` filtered by that register (+ its branch) + date range.
- Register performance ("Closed sessions" sub-view) rows → `/sales` filtered by that register + the session's own exact `openedAtUtc`..`closedAtUtc` window (not the report's date range — a session's own boundary is more precise and always available on the row).
- Cashier performance rows → `/sales` filtered by that cashier + date range.

**Timezone correctness constraint (from investigation):** `SalesPage`'s own `from`/`to` date pickers convert to UTC using the *browser's* local timezone, while every Performance report resolves its date range server-side in Asia/Manila via `ReportPeriodResolver`. A naive hand-off of period labels or date-only strings between the two would not reproduce identical boundaries. Each Performance result DTO (`BranchPerformanceResultDto`/`RegisterPerformanceResultDto`/`CashierPerformanceResultDto`) already carries the exact resolved `FromUtc`/`ToUtc` it used — the fix is to carry those exact instants through the link (as new `fromUtc`/`toUtc` override params `SalesPage` honors ahead of its own date-picker conversion), not to re-derive them.

`SalesPage`'s `Filters` type today only has `status`/`from`/`to`/`branchId` — `registerId` and `cashierUserId` must be added (the backend `SaleListQuery` and the `salesApi.list`/`SaleListParams` type already support both; only the page's own filter wiring is missing). No new backend work needed for this item.

`RegisterSessionReconciliationQuery` has no `CashierId` (a session is opened by one user, closed by another — not owned by a single cashier) — do not build a cashier-filtered link into the sessions sub-view; only a register + exact-window link.

## 3. Register sessions UX

- The sub-view toggle (Sales vs. Closed sessions) and the sessions list's own pagination are both local `useState`, not URL-synced — both reset on refresh. Sync both into the URL, following this app's existing conventions (`usePagedQuery` for pagination, a hand-rolled `useSearchParams` toggle like the page's own outer `tab` param for the two-way sub-view switch).
- The Cashier filter in the shared `ReportFilterBar` is a page-wide control that also renders while the Closed-sessions sub-view is active, where it is silently ignored server-side (confirmed: `RegisterSessionReconciliationQuery` has no `CashierId` field, so ASP.NET model binding drops it). Disable it with an explanatory tooltip when the Register tab's Closed-sessions sub-view is active, rather than leaving it clickable and inert.
- Of the reconciliation DTO's 18 fields, only 7 are currently rendered (Register, Closed, Closed by, Opening/Closing/Expected cash, Difference). Add "Opened" and "Opened by" as always-visible columns (pairs naturally with the existing Closed/Closed by). Add a "Branch" column only when the page is showing all branches (i.e., no branch filter is selected) — otherwise it's redundant. Surface the five cash-flow figures that explain a difference (`GrossCashSales`, `VoidedCashSales`, `RefundCashOut`, `CashIn`, `CashOut`) as an expandable per-row detail (click a row to reveal them) rather than five more always-on columns, to avoid overcrowding the main table.

## 4. Fulfillment-cancel approval reporting

`DeliveryReceipt.ApprovedByUserId` (added in commit `74a07a7`) is structurally identical to `Sale.ApprovedByUserId`/`SaleReturn.ApprovedByUserId`: `DeliveryReceipt` already carries its own `BranchId` (no join needed) and `CancelledAtUtc` as a natural date anchor (parallel to `Sale.VoidedAtUtc`/`SaleReturn.CreatedAtUtc`). Add a `FulfillmentCancelApprovalsCount` bucket to `CashierPerformanceRowDto`, built as a structural copy of the existing `voidApprovalsByApprover` query: grouped by `DeliveryReceipt.ApprovedByUserId` (non-null only), filtered by `CancelledAtUtc` within the resolved range, scoped by the same branch/register/cashier resolution `BaseSalesAsync` already performs (register/cashier scoping applied via `CancelledByUserId`, the actor column — matching this report's "attribute by action" convention, the same way `CashierId` filters `VoidedByUserId`/`CreatedByUserId` elsewhere in this method).

**Decision:** a separate, independently-keyed count — never folded into `VoidApprovalsCount` or `ReturnApprovalsCount`. Document it with the same doc-comment convention the existing two approval buckets use. Add the corresponding column to the Cashier Performance table, count-only (no value), matching the existing two approval columns' presentation.

## Out of scope

- No change to the whole-sale fulfillment domain model, checkout, or schedule-creation logic.
- No new index on `DeliveryReceipts.CancelledAtUtc`/`ApprovedByUserId` — the existing Void/Return approval queries filter on unindexed columns too (`Sale.VoidedAtUtc`, `SaleReturn.CreatedAtUtc`); this stays consistent with that precedent rather than introducing new inconsistency.
- No commit, merge, push, or branch deletion — left in the working tree for review.
