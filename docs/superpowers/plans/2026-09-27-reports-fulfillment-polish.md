# Reports + Fulfillment Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the four deferred polish items from Phases #12–14: stale delivery-report columns, missing performance drill-downs, inert sessions-view Cashier filter + non-URL-synced sub-view/pagination, and a missing fulfillment-cancel approvals count in Cashier Performance.

**Architecture:** Backend changes are narrow, additive DTO/query edits following patterns already established in the same file (`ReportsService.cs`/`ReportsContracts.cs`) — no new endpoints, no domain-model changes. Frontend changes extend three existing pages (`FulfillmentReportsPage.tsx`, `PerformanceReportsPage.tsx`, `SalesPage.tsx`) plus one new small shared helper for drill-down URLs. Every report continues to resolve dates server-side via the existing `ReportPeriodResolver` (Asia/Manila) and scope branches via the existing `IBranchAccessResolver` — nothing about that infrastructure changes.

**Tech Stack:** .NET 9 / EF Core 9 (backend), React 19 / TypeScript / TanStack Query v5 / React Router 7 (frontend), xUnit + FluentAssertions (tests).

**Spec:** `docs/superpowers/specs/2026-09-27-reports-fulfillment-polish.md`

## Global Constraints

- Do not modify `SaleItem.DeliveryRequiredQuantity`/`PickupRequiredQuantity`, checkout, or schedule-creation logic — the whole-sale fulfillment model stays exactly as-is.
- Every report continues to resolve date ranges via `ReportPeriodResolver` (Asia/Manila, `BusinessOffset = TimeSpan.FromHours(8)`) server-side — never re-derive a date range in the frontend.
- Every report continues to scope branches via `IBranchAccessResolver.ResolveListFilterAsync` (Owner/Admin see all-or-one-branch; Manager forced to their own branch) — do not bypass it.
- `FulfillmentCancelApprovalsCount` must be its own field, never folded into `VoidApprovalsCount`/`ReturnApprovalsCount`.
- No new EF index — stay consistent with the existing unindexed `Sale.VoidedAtUtc`/`SaleReturn.CreatedAtUtc` precedent.
- Do not commit, merge, push, or delete any branch — leave all changes in the working tree for review (per standing project rule).

---

## Task 1: Backend — simplify the delivery "Sale fulfillment" report DTO

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs` (lines 112-121 enum, 253-263 DTO)
- Modify: `src/Negosio.Application/Reports/ReportsService.cs` (lines 637-647 preset resolver, 798-819 row mapping)
- Test: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`

**Interfaces:**
- Produces: `DeliveryFulfillmentReportRowDto` with a new `bool NeedsScheduling` field replacing the four quantity fields; `DeliveryReportPreset` enum with `NeedsRescheduling` removed.

- [ ] **Step 1: Update the failing/changed tests first**

Open `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs` and find `Delivery_fulfillment_report_shows_one_row_per_sale_with_aggregated_quantities` (around line 359) and `A_sale_with_nothing_marked_for_delivery_never_appears_in_the_fulfillment_report` (around line 479). Rename the first to `Delivery_fulfillment_report_flags_a_sale_that_still_needs_scheduling` and replace its body's assertions on `TotalDeliveryRequiredQuantity`/`TotalPendingQuantity`/`TotalDeliveredQuantity`/`TotalUnscheduledQuantity` with assertions on `NeedsScheduling`:

```csharp
[Fact]
public async Task Delivery_fulfillment_report_flags_a_sale_that_still_needs_scheduling()
{
    // Arrange: checkout with Method = Delivery, but do NOT create a schedule afterward
    // (reuse whatever fixture helper this file already uses to build a delivery-flagged sale
    // without a DeliveryReceipt — check CheckoutAsync/CreateScheduleAsync usage earlier in this
    // file for the existing pattern).
    // ... existing arrange code that produces `sale` stays as-is ...

    var result = await _reportsService.GetDeliveryFulfillmentAsync(new DeliveryFulfillmentReportQuery());

    var row = result.Page.Items.Should().ContainSingle(r => r.SaleId == sale.Id).Subject;
    row.NeedsScheduling.Should().BeTrue("checkout succeeded but no schedule was ever created for it");
    row.ScheduleCount.Should().Be(0);
}

[Fact]
public async Task Delivery_fulfillment_report_does_not_flag_a_sale_with_an_active_schedule()
{
    // Arrange: same as above, but DO create a delivery schedule for the sale afterward.
    var result = await _reportsService.GetDeliveryFulfillmentAsync(new DeliveryFulfillmentReportQuery());

    var row = result.Page.Items.Should().ContainSingle(r => r.SaleId == sale.Id).Subject;
    row.NeedsScheduling.Should().BeFalse();
    row.ScheduleCount.Should().Be(1);
}
```

Keep `A_sale_with_nothing_marked_for_delivery_never_appears_in_the_fulfillment_report` as-is — it doesn't touch the removed fields.

- [ ] **Step 2: Run the tests to confirm they fail to compile** (the DTO doesn't have `NeedsScheduling` yet)

Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Delivery_fulfillment_report"`
Expected: build error, `NeedsScheduling` does not exist on `DeliveryFulfillmentReportRowDto`.

- [ ] **Step 3: Update the DTO and enum in `ReportsContracts.cs`**

Replace (around line 253):
```csharp
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
```
with:
```csharp
/// <summary>One sale that requires delivery. <see cref="NeedsScheduling"/> is true only in the one
/// edge case where checkout succeeded but the follow-up schedule-creation call never completed —
/// under the whole-sale model this is the only way a delivery-flagged sale can have zero active or
/// completed schedules. <see cref="ScheduleCount"/> counts every schedule ever created for this sale
/// (a cancel-and-reschedule chain), not just the current one.</summary>
public sealed record DeliveryFulfillmentReportRowDto(
    Guid SaleId,
    string SaleNumber,
    DateTime SaleCreatedAtUtc,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    bool NeedsScheduling,
    int ScheduleCount);
```

Replace the enum (around line 112):
```csharp
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
```
with (drop `NeedsRescheduling` — confirmed identical to `Overdue`):
```csharp
public enum DeliveryReportPreset
{
    All = 1,
    Today = 2,
    Upcoming = 3,
    Overdue = 4,
    Delivered = 5,
    Cancelled = 6,
}
```

- [ ] **Step 4: Update `ResolveDeliveryPreset` and the row mapping in `ReportsService.cs`**

Remove the now-invalid switch arm (around line 643):
```csharp
DeliveryReportPreset.NeedsRescheduling => (null, null, null, true),
```

Replace the mapping block (around lines 798-819):
```csharp
var mapped = rows.Select(r =>
{
    var available = r.TotalRequired - r.TotalPending - r.TotalDelivered;
    FulfillmentMethod? method = r.TotalDelivered > 0m || r.TotalPending > 0m ? FulfillmentMethod.Delivery : null;
    FulfillmentStatus? status = r.TotalDelivered > 0m ? FulfillmentStatus.Completed
        : r.TotalPending > 0m ? FulfillmentStatus.Pending
        : null;
    var fulfillmentStatus = SaleFulfillmentCalculator.Derive(method, status);
    return new
    {
        Dto = new DeliveryFulfillmentReportRowDto(
            r.Id, r.SaleNumber, r.CreatedAtUtc, fulfillmentStatus, r.DeliveryCharge,
            r.TotalRequired, r.TotalPending, r.TotalDelivered, available, r.ScheduleCount),
        r.HasOverduePending,
    };
});
```
with:
```csharp
var mapped = rows.Select(r =>
{
    var needsScheduling = r.TotalPending == 0m && r.TotalDelivered == 0m;
    FulfillmentMethod? method = r.TotalDelivered > 0m || r.TotalPending > 0m ? FulfillmentMethod.Delivery : null;
    FulfillmentStatus? status = r.TotalDelivered > 0m ? FulfillmentStatus.Completed
        : r.TotalPending > 0m ? FulfillmentStatus.Pending
        : null;
    var fulfillmentStatus = SaleFulfillmentCalculator.Derive(method, status);
    return new
    {
        Dto = new DeliveryFulfillmentReportRowDto(
            r.Id, r.SaleNumber, r.CreatedAtUtc, fulfillmentStatus, r.DeliveryCharge,
            needsScheduling, r.ScheduleCount),
        r.HasOverduePending,
    };
});
```
(the underlying `rows` query that computes `TotalRequired`/`TotalPending`/`TotalDelivered` per-sale, lines ~774-796, is unchanged — those are still needed as intermediate values to derive `needsScheduling` and `fulfillmentStatus`, they're just no longer exposed on the DTO).

- [ ] **Step 5: Build and run the tests again**

Run: `dotnet build src/Negosio.Api` then `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Delivery_fulfillment_report|FullyQualifiedName~A_sale_with_nothing_marked_for_delivery"`
Expected: PASS (all 3 tests).

- [ ] **Step 6: Commit is NOT performed by this task** — leave changes in the working tree (standing project rule). Move to Task 2.

---

## Task 2: Frontend — update the "Sale fulfillment" sub-view and delivery presets

**Files:**
- Modify: `web/negosio-web/src/api/types.ts` (lines 1056-1121 `DeliveryReportPreset`, lines 1154-1165 `DeliveryFulfillmentReportRowDto`)
- Modify: `web/negosio-web/src/pages/FulfillmentReportsPage.tsx` (lines 44-52 `DELIVERY_PRESETS`, lines 424-488 the fulfillment sub-view table)

**Interfaces:**
- Consumes: `DeliveryFulfillmentReportRowDto.needsScheduling: boolean` (from Task 1's backend change).

- [ ] **Step 1: Update `types.ts`**

Remove `NeedsRescheduling` from the `DeliveryReportPreset` union (find it — it's a string-literal union or enum-like type near line 1056; remove the `'NeedsRescheduling'` member to match the backend enum).

Replace (around line 1154):
```ts
export interface DeliveryFulfillmentReportRowDto {
  saleId: string
  saleNumber: string
  saleCreatedAtUtc: string
  fulfillmentStatus: SaleFulfillmentStatus
  deliveryCharge: number
  totalDeliveryRequiredQuantity: number
  totalPendingQuantity: number
  totalDeliveredQuantity: number
  totalUnscheduledQuantity: number
  scheduleCount: number
}
```
with:
```ts
export interface DeliveryFulfillmentReportRowDto {
  saleId: string
  saleNumber: string
  saleCreatedAtUtc: string
  fulfillmentStatus: SaleFulfillmentStatus
  deliveryCharge: number
  needsScheduling: boolean
  scheduleCount: number
}
```

- [ ] **Step 2: Update `DELIVERY_PRESETS` in `FulfillmentReportsPage.tsx`**

Remove this line (around line 51):
```tsx
{ value: 'NeedsRescheduling', label: 'Needs rescheduling' },
```

- [ ] **Step 3: Replace the four quantity columns with a single "Needs scheduling" indicator**

Replace the skeleton header (around lines 424-434) and real header (around lines 456-465):
```tsx
<Table.HeaderCell align="right">Required</Table.HeaderCell>
<Table.HeaderCell align="right">Pending</Table.HeaderCell>
<Table.HeaderCell align="right">Delivered</Table.HeaderCell>
<Table.HeaderCell align="right">Unscheduled</Table.HeaderCell>
```
with:
```tsx
<Table.HeaderCell>Needs scheduling</Table.HeaderCell>
```
(in both the skeleton block and the real header — and drop the skeleton's column-count `Array.from({ length: 8 })` down to `Array.from({ length: 5 })` to match the new 5-column layout: Sale #, Created, Status, Needs scheduling, Delivery charge).

Replace the row cells (around lines 480-483):
```tsx
<Table.Cell align="right">{formatQty(r.totalDeliveryRequiredQuantity)}</Table.Cell>
<Table.Cell align="right">{formatQty(r.totalPendingQuantity)}</Table.Cell>
<Table.Cell align="right">{formatQty(r.totalDeliveredQuantity)}</Table.Cell>
<Table.Cell align="right">{formatQty(r.totalUnscheduledQuantity)}</Table.Cell>
```
with:
```tsx
<Table.Cell>
  {r.needsScheduling ? (
    <Badge tone="warning">Needs scheduling</Badge>
  ) : (
    <span className="text-text-muted">—</span>
  )}
</Table.Cell>
```
(check the `Badge` component's `tone` prop options in `web/negosio-web/src/components/ui` — use whichever existing tone reads as "attention needed", e.g. `warning` or `amber`, matching what `saleFulfillmentStatusTone` already uses elsewhere in this same file for consistency).

If `formatQty` becomes unused in this file after this change, remove its import from `../lib/format` at the top of the file (check the rest of the file first — it's also used by other sub-views? no, confirmed only used in the fulfillment sub-view rows removed above).

- [ ] **Step 4: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors.

- [ ] **Step 5: Visual check** — not required for this step in isolation; covered by Task 10's full manual pass.

---

## Task 3: Backend — add `FulfillmentCancelApprovalsCount` to Cashier Performance

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs` (lines 367-405 `CashierPerformanceRowDto` + doc comment)
- Modify: `src/Negosio.Application/Reports/ReportsService.cs` (lines 393-496 `GetCashierPerformanceAsync`)
- Test: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`

**Interfaces:**
- Produces: `CashierPerformanceRowDto.FulfillmentCancelApprovalsCount: int`.

- [ ] **Step 1: Write the failing tests first**

Add to `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`, near the existing `GetCashierPerformanceAsync_VoidApprovalIsAttributedToTheApprovingManagerNotTheVoidingCashier` test (around line 746) — mirror its exact structure, substituting a fulfillment-cancel-under-approval flow for a void-under-approval flow:

```csharp
[Fact]
public async Task GetCashierPerformanceAsync_FulfillmentCancelApprovalIsAttributedToTheApprovingManagerNotTheCancellingCashier()
{
    // Arrange: create a Cashier WITHOUT the FulfillmentCancel grant and a Manager, both in the same
    // branch. Check out a Delivery sale as the Cashier, create its schedule, then have the Cashier
    // cancel it supplying the Manager's approval credentials (mirror how
    // Cashier_without_grant_requires_approval_then_succeeds_with_valid_manager_credentials in
    // DeliveryReceiptStatusTests.cs sets this up — same fixtures, this file just asserts the
    // reporting side instead of the cancel response).

    var result = await _reportsService.GetCashierPerformanceAsync(new ReportFilter());

    var cashierRow = result.Rows.Should().ContainSingle(r => r.CashierUserId == cashier.Id).Subject;
    var managerRow = result.Rows.Should().ContainSingle(r => r.CashierUserId == manager.Id).Subject;

    cashierRow.FulfillmentCancelApprovalsCount.Should().Be(0,
        "the Cashier was not the approver of their own cancellation");
    managerRow.FulfillmentCancelApprovalsCount.Should().Be(1,
        "the Manager approved the cancellation — a distinct bucket from who performed it");
}

[Fact]
public async Task GetCashierPerformanceAsync_FulfillmentCancelApprovalsRespectBranchScoping()
{
    // Arrange: same approval flow as above, but in Branch A. Query GetCashierPerformanceAsync with
    // a filter forced to Branch B (mirror how this file's existing branch-scoping tests, e.g. for
    // void/return approvals or GetBranchPerformanceAsync, force a different branch — grep this file
    // for "BranchId =" in a ReportFilter construction for the pattern).

    var result = await _reportsService.GetCashierPerformanceAsync(new ReportFilter { BranchId = branchB.Id });

    result.Rows.Should().NotContain(r => r.CashierUserId == manager.Id,
        "the approval happened in Branch A and this query is scoped to Branch B");
}
```

- [ ] **Step 2: Run to confirm compile failure**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~FulfillmentCancelApproval"`
Expected: build error — `FulfillmentCancelApprovalsCount` does not exist yet.

- [ ] **Step 3: Add the field to `CashierPerformanceRowDto`**

Extend the doc comment (append to the existing one ending "...an approver isn't financially 'responsible' for the voided/refunded amount the way the actor is.") with:
```
/// A SIXTH bucket, <see cref="FulfillmentCancelApprovalsCount"/>, follows the identical shape for
/// fulfillment-schedule cancellations — grouped by
/// <see cref="Negosio.Domain.Entities.Delivery.DeliveryReceipt.ApprovedByUserId"/>, filtered by
/// <see cref="Negosio.Domain.Entities.Delivery.DeliveryReceipt.CancelledAtUtc"/>, scoped by
/// <see cref="Negosio.Domain.Entities.Delivery.DeliveryReceipt.BranchId"/> directly (no Sale join
/// needed, same as <c>SaleReturn</c>). Never folded into <see cref="VoidApprovalsCount"/> or
/// <see cref="ReturnApprovalsCount"/> — a cashier who cancels a delivery schedule under Manager
/// approval is a fully independent event from voiding or returning a sale.
```

Add the field to the record (after `ReturnApprovalsCount`):
```csharp
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
    int ReturnApprovalsCount,
    int FulfillmentCancelApprovalsCount);
```

- [ ] **Step 4: Add the query in `GetCashierPerformanceAsync`**

After the existing `returnApprovalsByApprover` query (around line 454), add a structurally identical query scoped to `DeliveryReceipts` instead of `Sales`/`SaleReturns`. First resolve the same branch/register/cashier scoping this method already uses — reuse `_branchAccess.ResolveListFilterAsync(filter.BranchId, cancellationToken)` (check how `BaseSalesAsync`/`BaseReturnsAsync` resolve it at the top of this file and call it the same way here, or factor a tiny local `IQueryable<DeliveryReceipt>` builder if this service already has a `DeliveryReceipts` base-query helper — check for one before adding a new one):

```csharp
var branchFilter = await _branchAccess.ResolveListFilterAsync(filter.BranchId, cancellationToken);
var fulfillmentCancelsBase = _db.DeliveryReceipts.AsNoTracking().Where(d => d.TenantId == tenantId);
if (branchFilter is { } scopedBranchId)
{
    fulfillmentCancelsBase = fulfillmentCancelsBase.Where(d => d.BranchId == scopedBranchId);
}
if (filter.RegisterId is { } registerFilterId)
{
    // A DeliveryReceipt has no direct RegisterId — scope through the linked Sale's register the
    // same way BaseReturnsAsync resolves its own RegisterId filter through Sale. Check
    // BaseReturnsAsync's exact join pattern (it resolves register via the sale's RegisterSession)
    // and mirror it here rather than re-deriving it — if BaseReturnsAsync exposes a reusable
    // private helper for "given a SaleId queryable, filter to this RegisterId", call that instead
    // of duplicating the join logic inline.
}
if (filter.CashierId is { } cashierFilterId)
{
    fulfillmentCancelsBase = fulfillmentCancelsBase.Where(d => d.CancelledByUserId == cashierFilterId);
}

// Fulfillment-cancel approvals — a SIXTH sibling bucket to void/return approvals above, grouped by
// ApprovedByUserId (the approver), filtered to non-null (null means the actor cancelled under their
// own direct authority — Owner/Admin/Manager, or a granted Cashier). CancelledAtUtc is the natural
// date anchor here, same role VoidedAtUtc/SaleReturn.CreatedAtUtc play for the other two buckets.
var fulfillmentCancelApprovalsByApprover = await fulfillmentCancelsBase
    .Where(d => d.CancelledAtUtc != null
        && d.CancelledAtUtc >= range.FromUtc && d.CancelledAtUtc < range.ToUtc && d.ApprovedByUserId != null)
    .GroupBy(d => d.ApprovedByUserId!.Value)
    .Select(g => new { ApproverUserId = g.Key, Count = g.Count() })
    .ToListAsync(cancellationToken);
```

Fold it into the union and row construction:
```csharp
var userIds = salesByCashier.Select(r => r.CashierUserId)
    .Union(voidedByActor.Select(r => r.ActorUserId))
    .Union(returnsByActor.Select(r => r.ActorUserId))
    .Union(voidApprovalsByApprover.Select(r => r.ApproverUserId))
    .Union(returnApprovalsByApprover.Select(r => r.ApproverUserId))
    .Union(fulfillmentCancelApprovalsByApprover.Select(r => r.ApproverUserId))
    .Distinct()
    .ToList();
```
and, inside the `rows = userIds.Select(id => { ... })` block:
```csharp
var fca = fulfillmentCancelApprovalsByApprover.FirstOrDefault(x => x.ApproverUserId == id);
...
return new CashierPerformanceRowDto(
    id,
    userNames.TryGetValue(id, out var name) ? name : "(unknown user)",
    s?.GrossSales ?? 0m,
    netSales,
    count,
    count == 0 ? 0m : Money.Round(netSales / count),
    s?.Discounts ?? 0m,
    r?.Count ?? 0,
    r?.Value ?? 0m,
    v?.Count ?? 0,
    v?.Value ?? 0m,
    va?.Count ?? 0,
    ra?.Count ?? 0,
    fca?.Count ?? 0);
```

If `filter.RegisterId` scoping through `DeliveryReceipt` turns out to have no existing reusable helper (Step 4's inline TODO above), it is acceptable for a first cut to leave the `RegisterId` filter NOT applied to this one bucket (document why in a one-line comment: "a DeliveryReceipt has no direct register — approvals aren't currently register-filterable, only branch/cashier") rather than inventing a new, untested join. Confirm with whichever reviewer runs after this task whether that gap is acceptable or needs a follow-up.

- [ ] **Step 5: Run tests**

Run: `dotnet build src/Negosio.Api` then `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~FulfillmentCancelApproval"`
Expected: PASS (2 tests).

- [ ] **Step 6: Run the full existing Cashier Performance test suite to check for regressions**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~GetCashierPerformanceAsync"`
Expected: all PASS (existing + 2 new).

---

## Task 4: Frontend — surface `fulfillmentCancelApprovalsCount` in Cashier Performance

**Files:**
- Modify: `web/negosio-web/src/api/types.ts` (lines 1337-1353 `CashierPerformanceRowDto`)
- Modify: `web/negosio-web/src/pages/PerformanceReportsPage.tsx` (lines 64-74 sort accessors, 457-484 skeleton, 493-522 header, 524-540 body)

**Interfaces:**
- Consumes: `CashierPerformanceRowDto.fulfillmentCancelApprovalsCount: number` (from Task 3).

- [ ] **Step 1: Update the TS type**

Add to the interface (after `returnApprovalsCount`):
```ts
export interface CashierPerformanceRowDto {
  // ... existing fields ...
  voidApprovalsCount: number
  returnApprovalsCount: number
  fulfillmentCancelApprovalsCount: number
}
```

- [ ] **Step 2: Add the sort accessor**

```ts
const cashierSortAccessors: Record<string, (r: CashierPerformanceRowDto) => number> = {
  // ... existing entries ...
  voidApprovalsCount: (r) => r.voidApprovalsCount,
  returnApprovalsCount: (r) => r.returnApprovalsCount,
  fulfillmentCancelApprovalsCount: (r) => r.fulfillmentCancelApprovalsCount,
}
```

- [ ] **Step 3: Add the column to the skeleton, header, and body**

Skeleton (bump `Array.from({ length: 10 })` to `Array.from({ length: 11 })`) and add a header cell to the skeleton's `<Table.Head>` list (a plain `<Table.HeaderCell align="right">Fulfillment cancel approvals</Table.HeaderCell>` with no sort props, matching the other skeleton headers' style).

Real header (after the "Return approvals" header cell):
```tsx
<Table.HeaderCell align="right" sortKey="fulfillmentCancelApprovalsCount" activeSort={activeSort} onSort={onSort}>
  Fulfillment cancel approvals
</Table.HeaderCell>
```

Body (after the `returnApprovalsCount` cell):
```tsx
<Table.Cell align="right">{r.fulfillmentCancelApprovalsCount}</Table.Cell>
```

- [ ] **Step 4: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors.

---

## Task 5: Frontend — extend `SalesPage` to accept register/cashier/exact-UTC-range drill-down filters

**Files:**
- Modify: `web/negosio-web/src/pages/SalesPage.tsx` (lines 22-70 `Filters` type + query construction)

**Interfaces:**
- Produces: `SalesPage` now reads `registerId`, `cashierUserId`, `fromUtc`, `toUtc` from the URL (via `usePagedQuery`'s existing filter mechanism) and, when `fromUtc`/`toUtc` are present, uses them directly instead of recomputing from the `from`/`to` date-only pickers.
- Consumes: nothing new from the backend — `salesApi.list`/`SaleListParams` (`registerId`, `cashierUserId`, `fromUtc`, `toUtc`) already exist and are already honored by `SaleListQuery` server-side.

- [ ] **Step 1: Extend the `Filters` type and defaults**

```tsx
type Filters = {
  status: string | undefined
  from: string | undefined
  to: string | undefined
  branchId: string | undefined
  registerId: string | undefined
  cashierUserId: string | undefined
  // Exact UTC instants, set only by a drill-down link from a Performance report — when present,
  // these take precedence over `from`/`to` (which convert using the BROWSER's local timezone and
  // would not reproduce a report's Asia/Manila-resolved boundaries exactly).
  fromUtc: string | undefined
  toUtc: string | undefined
}

const DEFAULT_FILTERS: Filters = {
  status: undefined,
  from: undefined,
  to: undefined,
  branchId: undefined,
  registerId: undefined,
  cashierUserId: undefined,
  fromUtc: undefined,
  toUtc: undefined,
}
```

- [ ] **Step 2: Prefer `fromUtc`/`toUtc` when present in the query construction**

Replace:
```tsx
queryFn: () =>
  salesApi.list({
    page: q.page,
    pageSize: q.pageSize,
    search: q.search || undefined,
    status: (q.filters.status as SaleStatus) || undefined,
    branchId: q.filters.branchId,
    // Whole-day bounds in the browser's local zone (no tenant-timezone model yet — accepted).
    fromUtc: q.filters.from ? new Date(`${q.filters.from}T00:00:00.000`).toISOString() : undefined,
    toUtc: q.filters.to ? new Date(`${q.filters.to}T23:59:59.999`).toISOString() : undefined,
  }),
```
with:
```tsx
queryFn: () =>
  salesApi.list({
    page: q.page,
    pageSize: q.pageSize,
    search: q.search || undefined,
    status: (q.filters.status as SaleStatus) || undefined,
    branchId: q.filters.branchId,
    registerId: q.filters.registerId,
    cashierUserId: q.filters.cashierUserId,
    // A drill-down link's exact fromUtc/toUtc (Asia/Manila-resolved by the source report) takes
    // precedence over the from/to date pickers, which convert using the browser's local zone.
    fromUtc: q.filters.fromUtc ?? (q.filters.from ? new Date(`${q.filters.from}T00:00:00.000`).toISOString() : undefined),
    toUtc: q.filters.toUtc ?? (q.filters.to ? new Date(`${q.filters.to}T23:59:59.999`).toISOString() : undefined),
  }),
```
Also add `registerId: q.filters.registerId, cashierUserId: q.filters.cashierUserId, fromUtc: q.filters.fromUtc, toUtc: q.filters.toUtc` to the `queryKey` array above it, alongside the existing filter fields, so the query re-fetches when a drill-down link changes them.

- [ ] **Step 3: Add a drill-down banner**

Above the `<h1>` or directly below it, add a dismissible banner shown only when a drill-down filter is active:

```tsx
const hasDrilldownFilter = Boolean(q.filters.registerId || q.filters.cashierUserId || q.filters.fromUtc)

// ... in the JSX, right after the <h1>Sales</h1> line ...
{hasDrilldownFilter && (
  <div className="flex items-center justify-between rounded-lg border border-primary-200 bg-primary-50 px-3 py-2 text-[13px] text-primary-900">
    <span>Showing sales filtered from a performance report.</span>
    <Link to="/sales" className="font-semibold underline">
      Clear filter
    </Link>
  </div>
)}
```
(`Link` is already imported at the top of this file.)

- [ ] **Step 4: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors.

- [ ] **Step 5: Manual check**

Start the frontend dev server and visit `/sales?registerId=<a-real-register-id>&fromUtc=2026-01-01T00:00:00.000Z&toUtc=2026-01-02T00:00:00.000Z` directly — confirm the banner shows, "Clear filter" returns to a plain `/sales`, and the list actually narrows (check the Network tab request query string includes `registerId`/`fromUtc`/`toUtc`).

---

## Task 6: Frontend — add a shared drill-down URL helper

**Files:**
- Create: `web/negosio-web/src/lib/reportsDrilldown.ts`

**Interfaces:**
- Produces: `buildSalesDrilldownUrl(params: { fromUtc: string; toUtc: string; branchId?: string; registerId?: string; cashierUserId?: string }): string`, used by Task 7.

- [ ] **Step 1: Write the helper**

```ts
/** Builds a `/sales` URL carrying the exact filters a Performance report row represents. Always
 * passes fromUtc/toUtc explicitly (never a period label or date-only string) — see
 * docs/superpowers/specs/2026-09-27-reports-fulfillment-polish.md §2 for why: SalesPage's own
 * date pickers convert using the browser's local timezone, while every Performance report resolves
 * its range server-side in Asia/Manila, so only passing the already-resolved UTC instants through
 * guarantees the drill-down shows exactly the sales the report row summarized. */
export function buildSalesDrilldownUrl(params: {
  fromUtc: string
  toUtc: string
  branchId?: string
  registerId?: string
  cashierUserId?: string
}): string {
  const search = new URLSearchParams()
  search.set('fromUtc', params.fromUtc)
  search.set('toUtc', params.toUtc)
  if (params.branchId) search.set('branchId', params.branchId)
  if (params.registerId) search.set('registerId', params.registerId)
  if (params.cashierUserId) search.set('cashierUserId', params.cashierUserId)
  return `/sales?${search.toString()}`
}
```

- [ ] **Step 2: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors (this is a new, self-contained file with no other dependents yet).

---

## Task 7: Frontend — wire drill-down links into the Performance tables

**Files:**
- Modify: `web/negosio-web/src/pages/PerformanceReportsPage.tsx` (Branch table body ~211-226, Register-sales table body ~324-338, Cashier table body ~524-541, Register-sessions table body ~412-422)

**Interfaces:**
- Consumes: `buildSalesDrilldownUrl` (Task 6), `result.fromUtc`/`result.toUtc` already returned by every Performance query (`BranchPerformanceResultDto`/`RegisterPerformanceResultDto`/`CashierPerformanceResultDto`).

- [ ] **Step 1: Import the helper and `Link`**

Add near the top of `PerformanceReportsPage.tsx`:
```tsx
import { Link } from 'react-router-dom'
import { buildSalesDrilldownUrl } from '../lib/reportsDrilldown'
```

- [ ] **Step 2: Branch performance rows**

In `BranchPerformanceTab`, the query result is `query.data` (typed `BranchPerformanceResultDto`, has `.fromUtc`/`.toUtc`/`.rows`). Replace the branch-name cell:
```tsx
<Table.Cell>{r.branchName}</Table.Cell>
```
with:
```tsx
<Table.Cell>
  <Link
    to={buildSalesDrilldownUrl({ fromUtc: query.data.fromUtc, toUtc: query.data.toUtc, branchId: r.branchId })}
    className="font-semibold text-primary-700 hover:underline"
  >
    {r.branchName}
  </Link>
</Table.Cell>
```

- [ ] **Step 3: Register performance (Sales sub-view) rows**

In `RegisterSalesView`, same pattern — replace the register-name cell:
```tsx
<Table.Cell>{r.registerName}</Table.Cell>
```
with:
```tsx
<Table.Cell>
  <Link
    to={buildSalesDrilldownUrl({
      fromUtc: query.data.fromUtc,
      toUtc: query.data.toUtc,
      branchId: r.branchId,
      registerId: r.registerId,
    })}
    className="font-semibold text-primary-700 hover:underline"
  >
    {r.registerName}
  </Link>
</Table.Cell>
```

- [ ] **Step 4: Cashier performance rows**

In `CashierPerformanceTab`, replace the cashier-name cell:
```tsx
<Table.Cell>{r.cashierName}</Table.Cell>
```
with:
```tsx
<Table.Cell>
  <Link
    to={buildSalesDrilldownUrl({ fromUtc: query.data.fromUtc, toUtc: query.data.toUtc, cashierUserId: r.cashierUserId })}
    className="font-semibold text-primary-700 hover:underline"
  >
    {r.cashierName}
  </Link>
</Table.Cell>
```

- [ ] **Step 5: Register-sessions rows — link using the session's OWN window, not the report's range**

In `RegisterSessionsView`, replace the register-name cell:
```tsx
<Table.Cell>{r.registerName}</Table.Cell>
```
with:
```tsx
<Table.Cell>
  <Link
    to={buildSalesDrilldownUrl({ fromUtc: r.openedAtUtc, toUtc: r.closedAtUtc, registerId: r.registerId, branchId: r.branchId })}
    className="font-semibold text-primary-700 hover:underline"
  >
    {r.registerName}
  </Link>
</Table.Cell>
```
(this is deliberately more precise than the report-level date range — a session's own `openedAtUtc`/`closedAtUtc` is exact, so the resulting `/sales` list shows exactly the sales rung up during that session, not the whole report period. No cashier filter here — confirmed in the spec that `RegisterSessionReconciliationQuery` has no cashier concept to link from.)

- [ ] **Step 6: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors. (Watch for `query.data` possibly being `undefined` at the type level inside the `isPending`/`isError` early-return guards already in each function — the links above are only reachable in the final render path, after those guards, so `query.data` is already narrowed to defined there; if TypeScript still complains, use `query.data!.fromUtc` matching whatever narrowing convention the rest of the file already uses for `query.data.rows.length` a few lines above each edit site.)

- [ ] **Step 7: Manual check**

Start both dev servers. On `/reports/performance`, click a branch name, a register name (Sales sub-view), a cashier name, and a register name (Closed sessions sub-view) — confirm each navigates to `/sales` with the drill-down banner showing and a narrowed, non-empty (for at least one of them) result set.

---

## Task 8: Frontend — URL-sync the Register tab's sub-view and sessions pagination

**Files:**
- Modify: `web/negosio-web/src/pages/PerformanceReportsPage.tsx` (lines 88-134 page-level state, 236-255 `RegisterPerformanceTab`, 344-434 `RegisterSessionsView`)

**Interfaces:**
- Produces: the Register tab's Sales/Closed-sessions choice and the sessions list's page number both survive a refresh.

- [ ] **Step 1: Lift the sub-view state to `PerformanceReportsPage` and URL-sync it, mirroring the existing outer `tab` param**

Replace the outer state block (lines 88-99):
```tsx
const [params, setParams] = useSearchParams()
const tab: Tab = params.get('tab') === 'registers' ? 'registers' : params.get('tab') === 'cashiers' ? 'cashiers' : 'branches'
const setTab = (next: Tab) => {
  setParams((prev) => {
    const p = new URLSearchParams(prev)
    if (next === 'branches') p.delete('tab')
    else p.set('tab', next)
    return p
  })
}
```
with:
```tsx
const [params, setParams] = useSearchParams()
const tab: Tab = params.get('tab') === 'registers' ? 'registers' : params.get('tab') === 'cashiers' ? 'cashiers' : 'branches'
const setTab = (next: Tab) => {
  setParams((prev) => {
    const p = new URLSearchParams(prev)
    if (next === 'branches') p.delete('tab')
    else p.set('tab', next)
    return p
  })
}

const registerView: 'sales' | 'sessions' = params.get('view') === 'sessions' ? 'sessions' : 'sales'
const setRegisterView = (next: 'sales' | 'sessions') => {
  setParams((prev) => {
    const p = new URLSearchParams(prev)
    if (next === 'sales') p.delete('view')
    else p.set('view', next)
    p.delete('sessionsPage')
    return p
  })
}
```

- [ ] **Step 2: Pass `registerView`/`setRegisterView` down instead of local state**

Replace `RegisterPerformanceTab`'s body:
```tsx
function RegisterPerformanceTab({ filters }: { filters: ReportFiltersState }) {
  const [view, setView] = useState<'sales' | 'sessions'>('sales')

  return (
    <div className="space-y-5">
      <div className="flex justify-end">
        <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
          <TabButton active={view === 'sales'} onClick={() => setView('sales')}>
            Sales
          </TabButton>
          <TabButton active={view === 'sessions'} onClick={() => setView('sessions')}>
            Closed sessions
          </TabButton>
        </div>
      </div>

      {view === 'sales' ? <RegisterSalesView filters={filters} /> : <RegisterSessionsView filters={filters} />}
    </div>
  )
}
```
with:
```tsx
function RegisterPerformanceTab({
  filters,
  view,
  setView,
}: {
  filters: ReportFiltersState
  view: 'sales' | 'sessions'
  setView: (next: 'sales' | 'sessions') => void
}) {
  return (
    <div className="space-y-5">
      <div className="flex justify-end">
        <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
          <TabButton active={view === 'sales'} onClick={() => setView('sales')}>
            Sales
          </TabButton>
          <TabButton active={view === 'sessions'} onClick={() => setView('sessions')}>
            Closed sessions
          </TabButton>
        </div>
      </div>

      {view === 'sales' ? <RegisterSalesView filters={filters} /> : <RegisterSessionsView filters={filters} />}
    </div>
  )
}
```
and update the call site (was `{tab === 'registers' && <RegisterPerformanceTab filters={filters} />}`):
```tsx
{tab === 'registers' && (
  <RegisterPerformanceTab filters={filters} view={registerView} setView={setRegisterView} />
)}
```
`useState` may now be unused for this purpose in this file — check the rest of the file (`useSortableRows` also uses `useState`) before removing the import; it will still be needed elsewhere.

- [ ] **Step 3: URL-sync the sessions list's own pagination**

Replace `RegisterSessionsView`'s pagination state:
```tsx
function RegisterSessionsView({ filters }: { filters: ReportFiltersState }) {
  const [page, setPage] = useState(1)
  const pageSize = 20

  // A filter change can leave `page` pointing past the new result set's last page (e.g. narrowing
  // the date range while on page 3) — reset to page 1 whenever the shared filters change.
  useEffect(() => {
    setPage(1)
  }, [filters.period, filters.fromDate, filters.toDate, filters.branchId, filters.registerId, filters.cashierId])
```
with:
```tsx
function RegisterSessionsView({ filters }: { filters: ReportFiltersState }) {
  const [params, setParams] = useSearchParams()
  const pageSize = 20
  const page = Math.max(1, Number(params.get('sessionsPage')) || 1)
  const setPage = (next: number) => {
    setParams((prev) => {
      const p = new URLSearchParams(prev)
      if (next <= 1) p.delete('sessionsPage')
      else p.set('sessionsPage', String(next))
      return p
    })
  }

  // A filter change can leave `page` pointing past the new result set's last page (e.g. narrowing
  // the date range while on page 3) — reset to page 1 whenever the shared filters change.
  useEffect(() => {
    setPage(1)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [filters.period, filters.fromDate, filters.toDate, filters.branchId, filters.registerId, filters.cashierId])
```
(`useSearchParams` is already imported at the top of this file for the outer `tab`/`view` state added in Step 1.)

- [ ] **Step 4: Type-check and manual check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors.

Manual: on `/reports/performance?tab=registers`, switch to Closed sessions, page to page 2, refresh the browser — confirm it stays on Closed sessions, page 2 (URL should read `?tab=registers&view=sessions&sessionsPage=2`).

---

## Task 9: Frontend — disable the inert Cashier filter on the sessions sub-view; expand reconciliation columns

**Files:**
- Modify: `web/negosio-web/src/components/reports/ReportFilterBar.tsx` (Cashier `<Select>`, lines 111-123)
- Modify: `web/negosio-web/src/pages/PerformanceReportsPage.tsx` (`ReportFilterBar` call site line 126, `RegisterSessionsView` table, lines 344-434)

**Interfaces:**
- Consumes: `registerView` from Task 8 (to know when the sessions sub-view is active).
- Produces: `ReportFilterBar` gains an optional `cashierFilterDisabled?: boolean` prop.

- [ ] **Step 1: Add the disable prop to `ReportFilterBar`**

```tsx
interface Props {
  filters: ReportFiltersState
  showBranchFilter: boolean
  /** True when the currently active sub-view has no per-cashier concept to filter by (e.g. the
   * Register tab's Closed-sessions sub-view — a session is opened by one user and closed by
   * another, not owned by a single cashier; RegisterSessionReconciliationQuery has no CashierId).
   * Disables the control with an explanatory tooltip instead of leaving it clickable and silently
   * ignored server-side. */
  cashierFilterDisabled?: boolean
}

export function ReportFilterBar({ filters, showBranchFilter, cashierFilterDisabled }: Props) {
  // ... unchanged ...

  <Select
    aria-label="Cashier"
    className="sm:w-44"
    value={filters.cashierId ?? ''}
    onChange={(e) => filters.setCashierId(e.target.value || undefined)}
    disabled={cashierFilterDisabled}
    title={cashierFilterDisabled ? 'Not applicable to closed-session reconciliation' : undefined}
  >
    <option value="">All cashiers</option>
    {cashiers.map((c) => (
      <option key={c.id} value={c.id}>
        {`${c.firstName ?? ''} ${c.lastName ?? ''}`.trim() || c.email}
      </option>
    ))}
  </Select>
```
(check the `Select` component's own prop types in `web/negosio-web/src/components/ui` — confirm it forwards `disabled`/`title` to the underlying `<select>`; if it's a thin wrapper around a native `<select>` this should already work with no changes there.)

- [ ] **Step 2: Pass the flag from `PerformanceReportsPage`**

```tsx
<ReportFilterBar
  filters={filters}
  showBranchFilter={showBranchFilter}
  cashierFilterDisabled={tab === 'registers' && registerView === 'sessions'}
/>
```

- [ ] **Step 3: Add Opened/Opened-by columns and a conditional Branch column**

Replace `RegisterSessionsView`'s header (both skeleton and real, keep them identical to each other as the existing code already does) and body. New column order: Register, [Branch — only when `!filters.branchId`], Opened, Opened by, Closed, Closed by, Opening cash, Closing cash, Expected, Difference.

```tsx
const showBranchColumn = !filters.branchId

// header (repeat in both skeleton and real):
<Table.HeaderCell>Register</Table.HeaderCell>
{showBranchColumn && <Table.HeaderCell>Branch</Table.HeaderCell>}
<Table.HeaderCell>Opened</Table.HeaderCell>
<Table.HeaderCell>Opened by</Table.HeaderCell>
<Table.HeaderCell>Closed</Table.HeaderCell>
<Table.HeaderCell>Closed by</Table.HeaderCell>
<Table.HeaderCell align="right">Opening cash</Table.HeaderCell>
<Table.HeaderCell align="right">Closing cash</Table.HeaderCell>
<Table.HeaderCell align="right">Expected</Table.HeaderCell>
<Table.HeaderCell align="right">Difference</Table.HeaderCell>

// body:
<Table.Row key={r.sessionId}>
  <Table.Cell>
    <Link to={buildSalesDrilldownUrl({ fromUtc: r.openedAtUtc, toUtc: r.closedAtUtc, registerId: r.registerId, branchId: r.branchId })} className="font-semibold text-primary-700 hover:underline">
      {r.registerName}
    </Link>
  </Table.Cell>
  {showBranchColumn && <Table.Cell>{r.branchName}</Table.Cell>}
  <Table.Cell>{new Date(r.openedAtUtc).toLocaleString()}</Table.Cell>
  <Table.Cell>{r.openedByName}</Table.Cell>
  <Table.Cell>{new Date(r.closedAtUtc).toLocaleString()}</Table.Cell>
  <Table.Cell>{r.closedByName}</Table.Cell>
  <Table.Cell align="right">{formatMoney(r.openingCash)}</Table.Cell>
  <Table.Cell align="right">{formatMoney(r.closingCash)}</Table.Cell>
  <Table.Cell align="right">{formatMoney(r.expectedCash)}</Table.Cell>
  <Table.Cell align="right">{formatMoney(r.cashDifference)}</Table.Cell>
</Table.Row>
```
(this folds in Task 7 Step 5's link change too — if Task 7 already applied it, just add the new columns around the existing linked cell rather than duplicating it.)

Update both skeletons' `Array.from({ length: N })` column counts to match the new header count (7 or 8 depending on `showBranchColumn` — since the skeleton renders before data loads and `filters.branchId` is already known synchronously from the URL, it can compute `showBranchColumn` the same way).

- [ ] **Step 4: Add an expandable cash-flow detail row**

Add local state for which row is expanded, and a click handler on each row (excluding the register-name link itself):

```tsx
const [expandedSessionId, setExpandedSessionId] = useState<string | null>(null)

// ... in the row, add onClick to toggle, and a chevron indicator; add a second <Table.Row> right
// after each session row when expanded:
<Table.Row
  key={r.sessionId}
  className="cursor-pointer"
  onClick={() => setExpandedSessionId((cur) => (cur === r.sessionId ? null : r.sessionId))}
>
  {/* ... existing cells as above ... */}
</Table.Row>
{expandedSessionId === r.sessionId && (
  <Table.Row key={`${r.sessionId}-detail`}>
    <Table.Cell colSpan={showBranchColumn ? 9 : 8} className="bg-surface-subtle">
      <div className="flex flex-wrap gap-x-6 gap-y-1 py-1 text-[13px]">
        <span>Gross cash sales: <strong>{formatMoney(r.grossCashSales)}</strong></span>
        <span>Voided cash sales: <strong>{formatMoney(r.voidedCashSales)}</strong></span>
        <span>Refund cash out: <strong>{formatMoney(r.refundCashOut)}</strong></span>
        <span>Cash in: <strong>{formatMoney(r.cashIn)}</strong></span>
        <span>Cash out: <strong>{formatMoney(r.cashOut)}</strong></span>
      </div>
    </Table.Cell>
  </Table.Row>
)}
```
(check `Table.Cell`'s prop types support `colSpan` — if the shared `Table` component doesn't forward it, check `Table.Row`/`Table.Cell`'s definition in `web/negosio-web/src/components/ui` first and add `colSpan` support there if missing, following whatever prop-forwarding convention the rest of that component already uses.)

Make sure clicking the register-name `<Link>` inside the row does not also trigger the row's expand/collapse — add `onClick={(e) => e.stopPropagation()}` to the `<Link>` itself.

- [ ] **Step 5: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: no errors.

- [ ] **Step 6: Manual check**

On `/reports/performance?tab=registers&view=sessions`: confirm the Cashier filter is now visibly disabled with a tooltip on hover; confirm Opened/Opened-by show; confirm Branch only shows when no branch filter is selected; click a row to expand the cash-flow detail, click the register-name link separately to confirm it navigates instead of just expanding.

---

## Task 10: Full verification pass

**Files:** none (verification only).

- [ ] **Step 1: Run the full backend test suite**

Run: `dotnet test`
Expected: all tests pass except the known pre-existing flake `DeliveryReceiptConcurrencyTests.Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds` (confirmed pre-existing and unrelated in the handover — if it fails, re-run just that test in isolation to confirm it's the same known flake, not a new regression: `dotnet test --filter "FullyQualifiedName~Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds"`).

- [ ] **Step 2: Frontend type-check and build**

Run: `cd web/negosio-web && npx tsc -b && npm run build`
Expected: zero errors.

- [ ] **Step 3: Manual pass through the live app**

Start both dev servers (API + frontend). Walk through:
1. `/reports/fulfillment?tab=deliveries` → Sale fulfillment sub-view: confirm the 4 old quantity columns are gone, "Needs scheduling" shows correctly for a sale with no schedule vs. one with a schedule, and the "Needs rescheduling" preset option is gone from the Delivery schedule sub-view's filter.
2. `/reports/performance` → click through Branch, Register (Sales), Cashier row links → confirm each lands on `/sales` correctly filtered with the drill-down banner.
3. `/reports/performance?tab=registers&view=sessions` → click a session's register-name link → confirm it filters `/sales` to exactly that session's window; confirm Cashier filter is disabled with tooltip; confirm Opened/Opened-by show; expand a row's cash-flow detail.
4. Refresh the browser while on `tab=registers&view=sessions` at page 2 of sessions → confirm state survives.
5. `/reports/performance?tab=cashiers` → confirm the new "Fulfillment cancel approvals" column renders (0 for everyone is fine if no test data exists — the goal is confirming it renders without error).

- [ ] **Step 4: Report results**

Summarize: which of the 4 spec items are fully done, backend/frontend test counts (before vs. after), any limitations found during manual testing (e.g. the RegisterId-scoping gap flagged as acceptable-for-now in Task 3 Step 4), and confirm nothing was committed/pushed/merged.
