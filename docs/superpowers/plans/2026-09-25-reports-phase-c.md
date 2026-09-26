# Reports Phase C Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Branch, Register, and Cashier performance views to the existing Reports module, reusing its established conventions end to end.

**Architecture:** Three new backend query methods on the existing `IReportsService`/`ReportsService`, each grouping the same KPI aggregation the Overview report already uses by a different dimension (branch, register, cashier) instead of filtering to one. Register performance additionally needs a second, separately-granular endpoint for closed-session cash reconciliation, since a session's reconciliation figures are computed once at close time and cannot be split across a date range the way sale-level rows can. One new frontend page with three tabs, following `FulfillmentReportsPage.tsx`'s existing multi-tab convention.

**Tech Stack:** .NET 9 / EF Core 9 / SQL Server (LocalDB) backend; React 19 / TypeScript strict / Vite / TanStack Query v5 / Recharts frontend.

**Spec:** `docs/superpowers/specs/2026-09-25-reports-phase-c-and-fulfillment-fixes.md` (Part 2 and Part 3 — this plan implements Reports Phase C; Part 1's fulfillment fixes are a separate plan, `docs/superpowers/plans/2026-09-25-fulfillment-recovery-fixes.md`, and should be complete and merged into this branch's history before this plan starts, per the user's own stated ordering).

## Global Constraints

- Branch: `feature/pos-for-delivery`. **Do not merge to `master`. Do not push any branch.**
- Every new endpoint sits on the existing `[Authorize(Policy = AuthorizationPolicies.ReportsView)]` class-level policy on `ReportsController` — do not add a new policy, do not special-case any action.
- Every new query calls `IBranchAccessResolver.ResolveListFilterAsync` first, exactly as `ReportsService.BaseSalesAsync` already does — Manager must never see another branch's data regardless of what `branchId` is requested.
- Reuse `ReportPeriodResolver.Resolve(...)` for every date-range resolution — do not write a second timezone conversion anywhere in this plan's new code.
- Reuse the `Qualifying(salesBase, fromUtc, toUtc)` predicate (`CreatedAtUtc` in range AND `Status != Voided`) for every "current period" sales aggregate — do not reimplement it.
- Aggregate a split-tender sale's payments per payment record, not per sale (matches `ComputePaymentMethodsAsync`).
- Do not report a discount-approver breakdown anywhere — that identity is not persisted (see spec). Discount figures are amount/count only.
- Do not attempt to proportionally split a register session's reconciliation figures across a midnight boundary — report closed sessions as individual rows, dated by `ClosedAtUtc`'s business-local date.
- `dotnet test` must stay green after every task (starting baseline for this plan: whatever `dotnet test` reports after the fulfillment-fixes plan completes — record it at Task 1's start); `npx tsc -b` and `npm run build` must stay zero-error.
- Every task ends with a commit.

---

### Task 1: Backend — Branch performance

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs` (add to the existing file — it already knows how to seed multi-branch tenants/sales for this module's other tests)

**Interfaces:**
- Consumes: `ReportFilter` (existing), `ReportPeriodResolver.Resolve` (existing), `Qualifying` (existing private helper on `ReportsService`), `IBranchAccessResolver.ResolveListFilterAsync` (existing).
- Produces: `GET /api/reports/branch-performance` → `BranchPerformanceResultDto` — no other task in this plan depends on this task's exact shape, but Task 5 (frontend) consumes it, so keep field names exactly as defined here.

Read `ReportsService.cs`'s `GetOverviewAsync`, `Qualifying`, `BaseSalesAsync`, `BaseReturnsAsync`, and `ComputeKpisAsync` in full before writing this task's query — this task's method is structurally "the same KPI computation, grouped by branch instead of aggregated tenant-wide."

- [ ] **Step 1: Add the contracts**

In `src/Negosio.Application/Reports/ReportsContracts.cs`, add:

```csharp
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
```

Add the method signature to `IReportsService` (find its declaration in this same file or wherever the interface lives — likely alongside `GetOverviewAsync`):

```csharp
Task<BranchPerformanceResultDto> GetBranchPerformanceAsync(ReportFilter filter, CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing integration test**

Add to `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs` (open the file first and copy its exact multi-branch seeding helper — it must already have one, given the Overview report's own branch-filter tests):

```csharp
[Fact]
public async Task GetBranchPerformanceAsync_GroupsByBranchAndExcludesVoided()
{
    // Arrange: two branches, each with one completed sale; one branch also has a voided sale.
    var branchA = await SeedBranchWithSaleAsync(grandTotal: 500m);
    var branchB = await SeedBranchWithSaleAsync(grandTotal: 300m);
    await SeedVoidedSaleAsync(branchA.BranchId, grandTotal: 100m);

    var filter = new ReportFilter(ReportPeriod.Last30Days, null, null, BranchId: null, RegisterId: null, CashierId: null);

    // Act
    var result = await _reportsService.GetBranchPerformanceAsync(filter, CancellationToken.None);

    // Assert
    Assert.Equal(2, result.Rows.Count);
    var rowA = result.Rows.Single(r => r.BranchId == branchA.BranchId);
    Assert.Equal(500m, rowA.NetSales); // the voided 100 must not be included
    Assert.Equal(1, rowA.VoidedSalesCount);
    Assert.Equal(100m, rowA.VoidedSalesValue);
    var rowB = result.Rows.Single(r => r.BranchId == branchB.BranchId);
    Assert.Equal(300m, rowB.NetSales);
}
```

Adjust the seeding helper names to whatever this test file's actual existing helpers are called — read the file first rather than assuming `SeedBranchWithSaleAsync`/`SeedVoidedSaleAsync` exist verbatim; if no multi-branch seeding helper exists yet, write the minimal seed inline using this file's existing single-branch seeding pattern, called twice with two different branch ids.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetBranchPerformanceAsync"`
Expected: compile error (method doesn't exist yet) or, once stubbed, a failure.

- [ ] **Step 4: Implement the query**

In `src/Negosio.Application/Reports/ReportsService.cs`, add (placing it near `GetOverviewAsync` for locality):

```csharp
    public async Task<BranchPerformanceResultDto> GetBranchPerformanceAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var range = _periodResolver.Resolve(filter.Period, filter.FromDate, filter.ToDate);

        // Branch scoping: Owner/Admin may request any branch or leave it null (all branches); a
        // Manager's requested value is ignored and forced to their own assigned branch.
        var resolvedBranchId = await _branchAccess.ResolveListFilterAsync(filter.BranchId, ct);

        var salesBase = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (resolvedBranchId is { } branchId)
        {
            salesBase = salesBase.Where(s => s.BranchId == branchId);
        }

        var qualifying = Qualifying(salesBase, range.FromUtc, range.ToUtc);

        var salesRows = await qualifying
            .GroupBy(s => s.BranchId)
            .Select(g => new
            {
                BranchId = g.Key,
                GrossSales = g.Sum(s => s.Subtotal),
                NetSales = g.Sum(s => s.GrandTotal),
                Discounts = g.Sum(s => s.DiscountTotal),
                CompletedTransactions = g.Count(),
            })
            .ToListAsync(ct);

        var voidedRows = await salesBase
            .Where(s => s.Status == SaleStatus.Voided && s.VoidedAtUtc != null
                && s.VoidedAtUtc >= range.FromUtc && s.VoidedAtUtc < range.ToUtc)
            .GroupBy(s => s.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count(), Value = g.Sum(s => s.GrandTotal) })
            .ToListAsync(ct);

        var returnsBase = BaseReturnsAsync(tenantId, resolvedBranchId, filter.RegisterId, filter.CashierId);
        var returnsRows = await returnsBase
            .Where(r => r.CreatedAtUtc >= range.FromUtc && r.CreatedAtUtc < range.ToUtc)
            .Join(_db.Sales.AsNoTracking(), r => r.SaleId, s => s.Id, (r, s) => new { r.TotalRefund, s.BranchId })
            .GroupBy(x => x.BranchId)
            .Select(g => new { BranchId = g.Key, Count = g.Count(), Value = g.Sum(x => x.TotalRefund) })
            .ToListAsync(ct);

        var branchIds = salesRows.Select(r => r.BranchId)
            .Union(voidedRows.Select(r => r.BranchId))
            .Union(returnsRows.Select(r => r.BranchId))
            .Distinct()
            .ToList();
        var branchNames = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId && branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var rows = branchIds.Select(id =>
        {
            var s = salesRows.FirstOrDefault(r => r.BranchId == id);
            var v = voidedRows.FirstOrDefault(r => r.BranchId == id);
            var r = returnsRows.FirstOrDefault(r => r.BranchId == id);
            var netSales = s?.NetSales ?? 0m;
            var count = s?.CompletedTransactions ?? 0;
            return new BranchPerformanceRowDto(
                id,
                branchNames.TryGetValue(id, out var name) ? name : "(unknown branch)",
                s?.GrossSales ?? 0m,
                netSales,
                count,
                count == 0 ? 0m : Money.Round(netSales / count),
                s?.Discounts ?? 0m,
                r?.Count ?? 0,
                r?.Value ?? 0m,
                v?.Count ?? 0,
                v?.Value ?? 0m);
        })
        .OrderByDescending(r => r.NetSales)
        .ToList();

        return new BranchPerformanceResultDto(range.FromUtc, range.ToUtc, rows);
    }
```

Verify `BaseReturnsAsync`'s actual signature before using it — this plan assumes `BaseReturnsAsync(tenantId, branchId, registerId, cashierId)` returning `IQueryable<SaleReturn>` based on the investigation's description of it mirroring `BaseSalesAsync`, but read the real method to confirm parameter order/names/return type and adjust the call above to match exactly. Verify `Money.Round` is the actual name of this codebase's rounding helper (used elsewhere in this file per the investigation) rather than assuming.

- [ ] **Step 5: Add the controller endpoint**

In `src/Negosio.Api/Controllers/ReportsController.cs`, add (matching the existing endpoints' exact style — `[HttpGet]`, `[FromQuery] ReportFilter filter`):

```csharp
    [HttpGet("branch-performance")]
    public async Task<ActionResult<BranchPerformanceResultDto>> GetBranchPerformance(
        [FromQuery] ReportFilter filter, CancellationToken ct)
    {
        return Ok(await _reportsService.GetBranchPerformanceAsync(filter, ct));
    }
```

- [ ] **Step 6: Run to verify the test passes**

Run: `dotnet test --filter "FullyQualifiedName~GetBranchPerformanceAsync"`
Expected: PASS.

- [ ] **Step 7: Run the full backend suite**

Run: `dotnet test`. Expected: baseline + 1, 0 failures.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(reports): add Branch performance endpoint"
```

---

### Task 2: Backend — Register performance (sales/payment/cash-movement aggregate)

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`

**Interfaces:**
- Consumes: same shared helpers as Task 1, plus `RegisterCashMovement`/`CashMovementType` (`src/Negosio.Domain/Entities/Pos/RegisterCashMovement.cs`).
- Produces: `GET /api/reports/register-performance` → `RegisterPerformanceResultDto`.

This report groups by register within the resolved branch scope (a register belongs to exactly one branch, so a `registerId`-grouped query still needs the same branch access check as Task 1). Payment-method breakdown per register must follow the split-tender-safe, per-payment-record convention from `ComputePaymentMethodsAsync`.

- [ ] **Step 1: Add the contracts**

```csharp
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
```

Add to `IReportsService`:

```csharp
Task<RegisterPerformanceResultDto> GetRegisterPerformanceAsync(ReportFilter filter, CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing integration test**

```csharp
[Fact]
public async Task GetRegisterPerformanceAsync_GroupsByRegisterWithSplitTenderSafePaymentBreakdown()
{
    var register = await SeedRegisterAsync(); // existing or newly-written helper, returns (RegisterId, BranchId, RegisterSessionId)
    await SeedSplitTenderSaleAsync(register.RegisterSessionId, cashAmount: 200m, gcashAmount: 100m); // 300 total
    await SeedCashMovementAsync(register.RegisterSessionId, CashMovementType.CashIn, 50m);
    await SeedCashMovementAsync(register.RegisterSessionId, CashMovementType.CashOut, 20m);

    var filter = new ReportFilter(ReportPeriod.Last30Days, null, null, null, null, null);

    var result = await _reportsService.GetRegisterPerformanceAsync(filter, CancellationToken.None);

    var row = Assert.Single(result.Rows);
    Assert.Equal(300m, row.NetSales);
    Assert.Equal(2, row.PaymentMethods.Count); // Cash + GCash, each its own row
    Assert.Equal(200m, row.PaymentMethods.Single(p => p.Method == PaymentMethod.Cash).Amount);
    Assert.Equal(100m, row.PaymentMethods.Single(p => p.Method == PaymentMethod.GCash).Amount);
    Assert.Equal(50m, row.CashIn);
    Assert.Equal(20m, row.CashOut);
}
```

Adjust helper names/signatures to this test file's actual conventions — write new seeding helpers inline if none exist yet for split-tender sales or cash movements, following the file's existing single-payment seeding pattern as a template.

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetRegisterPerformanceAsync"`

- [ ] **Step 4: Implement the query**

```csharp
    public async Task<RegisterPerformanceResultDto> GetRegisterPerformanceAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var range = _periodResolver.Resolve(filter.Period, filter.FromDate, filter.ToDate);
        var resolvedBranchId = await _branchAccess.ResolveListFilterAsync(filter.BranchId, ct);

        var salesBase = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (resolvedBranchId is { } branchId)
        {
            salesBase = salesBase.Where(s => s.BranchId == branchId);
        }
        if (filter.RegisterId is { } requestedRegisterId)
        {
            salesBase = salesBase.Where(s => _db.RegisterSessions.Any(
                rs => rs.Id == s.RegisterSessionId && rs.RegisterId == requestedRegisterId));
        }

        var qualifying = Qualifying(salesBase, range.FromUtc, range.ToUtc);

        var salesByRegister = await (
            from s in qualifying
            join rs in _db.RegisterSessions.AsNoTracking() on s.RegisterSessionId equals rs.Id
            group s by rs.RegisterId into g
            select new
            {
                RegisterId = g.Key,
                GrossSales = g.Sum(s => s.Subtotal),
                NetSales = g.Sum(s => s.GrandTotal),
                CompletedTransactions = g.Count(),
            })
            .ToListAsync(ct);

        var paymentsByRegister = await (
            from p in _db.Payments.AsNoTracking()
            join s in qualifying on p.SaleId equals s.Id
            join rs in _db.RegisterSessions.AsNoTracking() on s.RegisterSessionId equals rs.Id
            group p by new { rs.RegisterId, p.Method } into g
            select new { g.Key.RegisterId, g.Key.Method, Amount = g.Sum(x => x.Amount), Count = g.Count() })
            .ToListAsync(ct);

        var cashMovementsByRegister = await _db.RegisterCashMovements.AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.CreatedAtUtc >= range.FromUtc && m.CreatedAtUtc < range.ToUtc)
            .Join(_db.RegisterSessions.AsNoTracking(), m => m.RegisterSessionId, rs => rs.Id, (m, rs) => new { m.Type, m.Amount, rs.RegisterId })
            .GroupBy(x => new { x.RegisterId, x.Type })
            .Select(g => new { g.Key.RegisterId, g.Key.Type, Total = g.Sum(x => x.Amount) })
            .ToListAsync(ct);

        var registerIds = salesByRegister.Select(r => r.RegisterId)
            .Union(paymentsByRegister.Select(r => r.RegisterId))
            .Union(cashMovementsByRegister.Select(r => r.RegisterId))
            .Distinct()
            .ToList();

        var registerInfo = await _db.Registers.AsNoTracking()
            .Where(r => r.TenantId == tenantId && registerIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Name, r.BranchId })
            .ToDictionaryAsync(r => r.Id, ct);
        var branchNames = await _db.Branches.AsNoTracking()
            .Where(b => b.TenantId == tenantId)
            .ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var rows = registerIds.Select(id =>
        {
            var s = salesByRegister.FirstOrDefault(r => r.RegisterId == id);
            var payments = paymentsByRegister.Where(p => p.RegisterId == id)
                .Select(p => new PaymentMethodBreakdownDto(p.Method, p.Amount, p.Count, 0m)) // Percentage not meaningful per-register; left 0
                .ToList();
            var cashIn = cashMovementsByRegister.FirstOrDefault(m => m.RegisterId == id && m.Type == CashMovementType.CashIn)?.Total ?? 0m;
            var cashOut = cashMovementsByRegister.FirstOrDefault(m => m.RegisterId == id && m.Type == CashMovementType.CashOut)?.Total ?? 0m;
            var netSales = s?.NetSales ?? 0m;
            var count = s?.CompletedTransactions ?? 0;
            var info = registerInfo.TryGetValue(id, out var i) ? i : null;
            return new RegisterPerformanceRowDto(
                id,
                info?.Name ?? "(unknown register)",
                info?.BranchId ?? Guid.Empty,
                info != null && branchNames.TryGetValue(info.BranchId, out var bn) ? bn : "(unknown branch)",
                s?.GrossSales ?? 0m,
                netSales,
                count,
                count == 0 ? 0m : Money.Round(netSales / count),
                payments,
                cashIn,
                cashOut);
        })
        .OrderByDescending(r => r.NetSales)
        .ToList();

        return new RegisterPerformanceResultDto(range.FromUtc, range.ToUtc, rows);
    }
```

Verify `PaymentMethodBreakdownDto`'s exact constructor parameter order/types against its real current definition before using it (it may not have a settable/nullable `Percentage` in the exact form assumed here — check whether passing `0m` compiles and is semantically acceptable, or whether the DTO needs a per-register percentage computed against that register's own total instead of being left at 0; if the latter is clearly better, compute `Amount / (payments.Sum(x => x.Amount))` per register instead of hardcoding 0). Verify `_db.Registers`/`_db.RegisterCashMovements` are the actual `DbSet` property names on `ITenantDbContext`.

- [ ] **Step 5: Add the controller endpoint**

```csharp
    [HttpGet("register-performance")]
    public async Task<ActionResult<RegisterPerformanceResultDto>> GetRegisterPerformance(
        [FromQuery] ReportFilter filter, CancellationToken ct)
    {
        return Ok(await _reportsService.GetRegisterPerformanceAsync(filter, ct));
    }
```

- [ ] **Step 6: Run to verify the test passes, then the full suite**

Run: `dotnet test --filter "FullyQualifiedName~GetRegisterPerformanceAsync"`, then `dotnet test` for the full suite.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(reports): add Register performance endpoint (sales, payment breakdown, cash movements)"
```

---

### Task 3: Backend — Register session reconciliation list

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`

**Interfaces:**
- Consumes: `RegisterSession`, `CashReconciliationBreakdown` (already exist), `PagedResult<T>.CreateAsync`.
- Produces: `GET /api/reports/register-sessions` → `PagedResult<RegisterSessionReconciliationRowDto>`.

This is deliberately a separate endpoint from Task 2, per the spec: a session's reconciliation figures are computed once at close time and persisted on the session row — they cannot be day-bucketed or merged into the sales-aggregate table without inventing a proportional split this plan explicitly avoids. Only **closed** sessions have reconciliation figures; an open session has none yet and is excluded here.

- [ ] **Step 1: Add the contracts**

```csharp
public sealed record RegisterSessionReconciliationQuery(
    ReportPeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    Guid? BranchId,
    Guid? RegisterId,
    int Page = 1,
    int PageSize = 20);

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
```

Add to `IReportsService`:

```csharp
Task<PagedResult<RegisterSessionReconciliationRowDto>> GetRegisterSessionReconciliationAsync(
    RegisterSessionReconciliationQuery query, CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing integration test**

```csharp
[Fact]
public async Task GetRegisterSessionReconciliationAsync_ReturnsOnlyClosedSessionsWithPersistedFigures()
{
    var (registerId, branchId) = await SeedRegisterAsync();
    var closedSessionId = await SeedAndCloseSessionAsync(registerId, openingCash: 1000m, closingCash: 1200m, expectedCash: 1200m);
    await SeedOpenSessionAsync(registerId, openingCash: 500m); // must NOT appear in results

    var query = new RegisterSessionReconciliationQuery(ReportPeriod.Last30Days, null, null, null, null, Page: 1, PageSize: 20);

    var result = await _reportsService.GetRegisterSessionReconciliationAsync(query, CancellationToken.None);

    var row = Assert.Single(result.Items);
    Assert.Equal(closedSessionId, row.SessionId);
    Assert.Equal(1000m, row.OpeningCash);
    Assert.Equal(1200m, row.ClosingCash);
    Assert.Equal(0m, row.CashDifference); // 1200 closing - 1200 expected
}
```

Adjust helper names to this file's actual conventions; if no session open/close helpers exist yet, write them using `IRegisterSessionService`'s real `OpenAsync`/`CloseAsync` methods (check their actual signatures first).

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetRegisterSessionReconciliationAsync"`

- [ ] **Step 4: Implement the query**

```csharp
    public async Task<PagedResult<RegisterSessionReconciliationRowDto>> GetRegisterSessionReconciliationAsync(
        RegisterSessionReconciliationQuery query, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var range = _periodResolver.Resolve(query.Period, query.FromDate, query.ToDate);
        var resolvedBranchId = await _branchAccess.ResolveListFilterAsync(query.BranchId, ct);

        var sessions = _db.RegisterSessions.AsNoTracking()
            .Where(s => s.TenantId == tenantId
                && s.Status == RegisterSessionStatus.Closed
                && s.ClosedAtUtc != null
                && s.ClosedAtUtc >= range.FromUtc && s.ClosedAtUtc < range.ToUtc);

        if (resolvedBranchId is { } branchId)
        {
            sessions = sessions.Where(s => s.BranchId == branchId);
        }
        if (query.RegisterId is { } registerId)
        {
            sessions = sessions.Where(s => s.RegisterId == registerId);
        }

        var projected =
            from s in sessions
            join r in _db.Registers.AsNoTracking() on s.RegisterId equals r.Id
            join b in _db.Branches.AsNoTracking() on s.BranchId equals b.Id
            join openedBy in _db.Users.AsNoTracking() on s.OpenedByUserId equals openedBy.Id
            join closedBy in _db.Users.AsNoTracking() on s.ClosedByUserId equals closedBy.Id into closedByJoin
            from closedBy in closedByJoin.DefaultIfEmpty()
            orderby s.ClosedAtUtc descending
            select new RegisterSessionReconciliationRowDto(
                s.Id, r.Id, r.Name, b.Id, b.Name,
                s.OpenedAtUtc, s.ClosedAtUtc!.Value,
                openedBy.FirstName + " " + openedBy.LastName,
                closedBy != null ? closedBy.FirstName + " " + closedBy.LastName : "(unknown)",
                s.OpeningCash, s.ClosingCash!.Value, s.ExpectedCash!.Value, s.CashDifference!.Value,
                s.GrossCashSales!.Value, s.VoidedCashSales!.Value, s.RefundCashOut!.Value,
                s.CashIn!.Value, s.CashOut!.Value);

        return await PagedResult<RegisterSessionReconciliationRowDto>.CreateAsync(projected, query.Page, query.PageSize, ct);
    }
```

`RegisterSession.ClosedByUserId` is nullable (per the domain entity) — this plan's `LEFT JOIN` (via `closedByJoin.DefaultIfEmpty()`) handles that. Verify `RegisterSession`'s exact nullable-vs-non-nullable fields against the real entity before assuming which properties need the `!.Value`/null-coalescing treatment shown above — the investigation reported `OpeningCash: decimal` (non-nullable, always set at Open) and `ClosingCash/ExpectedCash/CashDifference/GrossCashSales/VoidedCashSales/RefundCashOut/CashIn/CashOut: decimal?` (nullable, only set at Close) — since this query already filters to `Status == Closed`, every one of those nullable fields is guaranteed non-null in practice, but the C# type system doesn't know that, hence the `!.Value` — confirm this reasoning against the real entity rather than assuming this plan's nullability guess is exact.

- [ ] **Step 5: Add the controller endpoint**

```csharp
    [HttpGet("register-sessions")]
    public async Task<ActionResult<PagedResult<RegisterSessionReconciliationRowDto>>> GetRegisterSessionReconciliation(
        [FromQuery] RegisterSessionReconciliationQuery query, CancellationToken ct)
    {
        return Ok(await _reportsService.GetRegisterSessionReconciliationAsync(query, ct));
    }
```

- [ ] **Step 6: Run to verify the test passes, then the full suite**

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(reports): add paged closed-register-session reconciliation endpoint"
```

---

### Task 4: Backend — Cashier performance

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`

**Interfaces:**
- Consumes: same shared helpers as Task 1, grouped by `Sale.CreatedByUserId` instead of `BranchId`.
- Produces: `GET /api/reports/cashier-performance` → `CashierPerformanceResultDto`.

**Do not filter to `User.Role == Cashier`** — attribute by whoever's `Sale.CreatedByUserId` appears on a qualifying sale, so a Manager's or Owner's own checkout activity shows up too (see spec's explicit reasoning for this).

- [ ] **Step 1: Add the contracts**

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
    decimal VoidedSalesValue);

public sealed record CashierPerformanceResultDto(
    DateTime FromUtc,
    DateTime ToUtc,
    IReadOnlyList<CashierPerformanceRowDto> Rows);
```

Add to `IReportsService`:

```csharp
Task<CashierPerformanceResultDto> GetCashierPerformanceAsync(ReportFilter filter, CancellationToken ct = default);
```

- [ ] **Step 2: Write the failing integration test**

```csharp
[Fact]
public async Task GetCashierPerformanceAsync_AttributesByCheckoutUserRegardlessOfRole()
{
    var cashierUser = await SeedUserAsync(UserRole.Cashier);
    var managerUser = await SeedUserAsync(UserRole.Manager);
    await SeedSaleAsCheckoutUserAsync(cashierUser.Id, grandTotal: 400m);
    await SeedSaleAsCheckoutUserAsync(managerUser.Id, grandTotal: 250m); // a manager ringing up their own sale

    var filter = new ReportFilter(ReportPeriod.Last30Days, null, null, null, null, null);

    var result = await _reportsService.GetCashierPerformanceAsync(filter, CancellationToken.None);

    Assert.Equal(2, result.Rows.Count); // both the Cashier AND the Manager show up
    Assert.Equal(400m, result.Rows.Single(r => r.CashierUserId == cashierUser.Id).NetSales);
    Assert.Equal(250m, result.Rows.Single(r => r.CashierUserId == managerUser.Id).NetSales);
}

[Fact]
public async Task GetCashierPerformanceAsync_HistoricalSaleKeepsItsOriginalBranchRegardlessOfLaterReassignment()
{
    // Reframed per the spec: a User has at most one BranchId; there is no multi-branch assignment.
    // What must hold is that a Sale's own BranchId (fixed at checkout) is what the report's branch
    // scoping honors, not the cashier's CURRENT branch — verify by reassigning the cashier's branch
    // after checkout and confirming an Owner/Admin filtering Cashier performance by the ORIGINAL
    // branch still finds this cashier's historical sale.
    var branchA = await SeedBranchAsync();
    var branchB = await SeedBranchAsync();
    var cashier = await SeedUserAsync(UserRole.Cashier, branchId: branchA.Id);
    await SeedSaleAsCheckoutUserInBranchAsync(cashier.Id, branchA.Id, grandTotal: 400m);

    await ReassignUserBranchAsync(cashier.Id, branchB.Id); // the cashier moves to Branch B

    var filterForBranchA = new ReportFilter(ReportPeriod.Last30Days, null, null, BranchId: branchA.Id, null, null);
    var result = await _reportsService.GetCashierPerformanceAsync(filterForBranchA, CancellationToken.None);

    Assert.Contains(result.Rows, r => r.CashierUserId == cashier.Id && r.NetSales == 400m);
}
```

Write whatever seeding helpers this test file doesn't already have (`SeedUserAsync`, `SeedSaleAsCheckoutUserAsync`, `ReassignUserBranchAsync`) using this codebase's real user/sale creation APIs — check `StaffService`/`UserRepository`-equivalent and `CheckoutService` for the real methods to call rather than inventing a shortcut that bypasses real invariants (e.g. a sale must belong to a real, open `RegisterSession`).

- [ ] **Step 3: Run to verify both fail**

- [ ] **Step 4: Implement the query**

```csharp
    public async Task<CashierPerformanceResultDto> GetCashierPerformanceAsync(ReportFilter filter, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var range = _periodResolver.Resolve(filter.Period, filter.FromDate, filter.ToDate);
        var resolvedBranchId = await _branchAccess.ResolveListFilterAsync(filter.BranchId, ct);

        var salesBase = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (resolvedBranchId is { } branchId)
        {
            salesBase = salesBase.Where(s => s.BranchId == branchId);
        }
        if (filter.RegisterId is { } registerId)
        {
            salesBase = salesBase.Where(s => _db.RegisterSessions.Any(
                rs => rs.Id == s.RegisterSessionId && rs.RegisterId == registerId));
        }

        var qualifying = Qualifying(salesBase, range.FromUtc, range.ToUtc);

        var salesByCashier = await qualifying
            .GroupBy(s => s.CreatedByUserId)
            .Select(g => new
            {
                CashierUserId = g.Key,
                GrossSales = g.Sum(s => s.Subtotal),
                NetSales = g.Sum(s => s.GrandTotal),
                Discounts = g.Sum(s => s.DiscountTotal),
                CompletedTransactions = g.Count(),
            })
            .ToListAsync(ct);

        var voidedByCashier = await salesBase
            .Where(s => s.Status == SaleStatus.Voided && s.VoidedAtUtc != null
                && s.VoidedAtUtc >= range.FromUtc && s.VoidedAtUtc < range.ToUtc)
            .GroupBy(s => s.VoidedByUserId) // attribute void activity to whoever performed the void, not the original cashier
            .Where(g => g.Key != null)
            .Select(g => new { CashierUserId = g.Key!.Value, Count = g.Count(), Value = g.Sum(s => s.GrandTotal) })
            .ToListAsync(ct);

        var returnsByCashier = await _db.SaleReturns.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.CreatedAtUtc >= range.FromUtc && r.CreatedAtUtc < range.ToUtc)
            .Join(salesBase, r => r.SaleId, s => s.Id, (r, s) => r) // scope returns to the same branch/register filter as sales
            .GroupBy(r => r.CreatedByUserId)
            .Select(g => new { CashierUserId = g.Key, Count = g.Count(), Value = g.Sum(r => r.TotalRefund) })
            .ToListAsync(ct);

        var cashierIds = salesByCashier.Select(r => r.CashierUserId)
            .Union(voidedByCashier.Select(r => r.CashierUserId))
            .Union(returnsByCashier.Select(r => r.CashierUserId))
            .Distinct()
            .ToList();
        var cashierNames = await _db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && cashierIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);

        var rows = cashierIds.Select(id =>
        {
            var s = salesByCashier.FirstOrDefault(r => r.CashierUserId == id);
            var v = voidedByCashier.FirstOrDefault(r => r.CashierUserId == id);
            var r = returnsByCashier.FirstOrDefault(r => r.CashierUserId == id);
            var netSales = s?.NetSales ?? 0m;
            var count = s?.CompletedTransactions ?? 0;
            return new CashierPerformanceRowDto(
                id,
                cashierNames.TryGetValue(id, out var name) ? name : "(unknown user)",
                s?.GrossSales ?? 0m,
                netSales,
                count,
                count == 0 ? 0m : Money.Round(netSales / count),
                s?.Discounts ?? 0m,
                r?.Count ?? 0,
                r?.Value ?? 0m,
                v?.Count ?? 0,
                v?.Value ?? 0m);
        })
        .OrderByDescending(r => r.NetSales)
        .ToList();

        return new CashierPerformanceResultDto(range.FromUtc, range.ToUtc, rows);
    }
```

Note the deliberate choice above: void activity is grouped by `VoidedByUserId` (who performed the void), not `CreatedByUserId` (who rang up the original sale) — per the spec, void/return activity should show the actor who performed THAT action, which may differ from the original checkout cashier (e.g. a Manager voiding a Cashier's sale). This means a single voided sale can appear in one cashier's *sales* total and a different person's *void activity* total, which is correct and intentional, not a bug — confirm this reasoning still holds by re-reading the spec's "Void activity"/"Return activity" definitions before implementing, and write a test for this specific cross-attribution case if the two tests above don't already cover it (they don't — consider adding one, though it is not strictly required for this task to be considered complete if time-constrained, since it directly follows from the already-tested `Sale.VoidedByUserId`/`ApprovedByUserId` fields' existing, already-tested semantics).

- [ ] **Step 5: Add the controller endpoint**

```csharp
    [HttpGet("cashier-performance")]
    public async Task<ActionResult<CashierPerformanceResultDto>> GetCashierPerformance(
        [FromQuery] ReportFilter filter, CancellationToken ct)
    {
        return Ok(await _reportsService.GetCashierPerformanceAsync(filter, ct));
    }
```

- [ ] **Step 6: Run to verify both tests pass, then the full suite**

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(reports): add Cashier performance endpoint, attributed by checkout user regardless of role"
```

---

### Task 5: Frontend — Performance Reports page shell + Branch tab

**Files:**
- Create: `web/negosio-web/src/pages/PerformanceReportsPage.tsx`
- Modify: `web/negosio-web/src/api/reports.ts`
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/App.tsx` (route registration)
- Modify: `web/negosio-web/src/lib/nav.ts` (nav entry)

**Interfaces:**
- Consumes: `useReportFilters` (existing hook), `ReportFilterBar` (existing component), `Table`/`Table.HeaderCell`/`Pagination`/`EmptyState`/`ErrorState`/`SkeletonText` (existing, per Reports Overview/Fulfillment Reports conventions).
- Produces: the `PerformanceReportsPage` component and its `Tab` type (`'branches' | 'registers' | 'cashiers'`) — Tasks 6 and 7 add the other two tabs to this same file.

Read `web/negosio-web/src/pages/FulfillmentReportsPage.tsx`'s tab structure (the `Tab` type, the `TabButton`s, the `tab` state synced via `useSearchParams`) and `web/negosio-web/src/App.tsx`'s existing route registrations for `/reports` and the fulfillment reports route in full before writing this task — mirror both exactly rather than inventing a new pattern.

- [ ] **Step 1: Mirror the backend types**

In `web/negosio-web/src/api/types.ts`, add (matching Task 1's backend DTOs field-for-field, camelCased):

```typescript
export interface BranchPerformanceRowDto {
  branchId: string
  branchName: string
  grossSales: number
  netSales: number
  completedTransactions: number
  averageTransactionValue: number
  discounts: number
  returnsCount: number
  returnsValue: number
  voidedSalesCount: number
  voidedSalesValue: number
}

export interface BranchPerformanceResultDto {
  fromUtc: string
  toUtc: string
  rows: BranchPerformanceRowDto[]
}
```

- [ ] **Step 2: Add the API client function**

In `web/negosio-web/src/api/reports.ts`, add alongside the existing `overview`/`topProducts`/`categories` functions:

```typescript
branchPerformance: (params: ReportFilterParams) =>
  apiRequest<BranchPerformanceResultDto>(`/api/reports/branch-performance${qs({ ...params })}`),
```

(Import `BranchPerformanceResultDto` from `../api/types` at the top of the file, matching this file's existing import style.)

- [ ] **Step 3: Create the page shell with the Branches tab**

Create `web/negosio-web/src/pages/PerformanceReportsPage.tsx`:

```tsx
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { reportsApi } from '../api/reports'
import { useReportFilters } from '../hooks/useReportFilters'
import { ReportFilterBar } from '../components/reports/ReportFilterBar'
import { Table } from '../components/ui/Table'
import { Pagination } from '../components/ui/Pagination'
import { EmptyState } from '../components/ui/EmptyState'
import { ErrorState } from '../components/ui/ErrorState'
import { SkeletonText } from '../components/ui/SkeletonText'
import { formatMoney } from '../lib/format'

type Tab = 'branches' | 'registers' | 'cashiers'

export default function PerformanceReportsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const tab: Tab = searchParams.get('tab') === 'registers' ? 'registers' : searchParams.get('tab') === 'cashiers' ? 'cashiers' : 'branches'
  const setTab = (next: Tab) => setSearchParams((prev) => {
    const p = new URLSearchParams(prev)
    p.set('tab', next)
    return p
  }, { replace: true })

  const filters = useReportFilters()

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-bold text-text-primary">Performance Reports</h1>
      <div className="flex gap-2 border-b border-border-light">
        <TabButton active={tab === 'branches'} onClick={() => setTab('branches')}>Branches</TabButton>
        <TabButton active={tab === 'registers'} onClick={() => setTab('registers')}>Registers</TabButton>
        <TabButton active={tab === 'cashiers'} onClick={() => setTab('cashiers')}>Cashiers</TabButton>
      </div>
      <ReportFilterBar filters={filters} showBranchFilter={tab !== 'branches'} />
      {tab === 'branches' && <BranchPerformanceTab filters={filters} />}
      {tab === 'registers' && <RegisterPerformanceTab filters={filters} />}
      {tab === 'cashiers' && <CashierPerformanceTab filters={filters} />}
    </div>
  )
}

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`px-4 py-2 text-sm font-semibold border-b-2 ${active ? 'border-primary-500 text-primary-700' : 'border-transparent text-text-secondary hover:text-text-primary'}`}
    >
      {children}
    </button>
  )
}

function BranchPerformanceTab({ filters }: { filters: ReturnType<typeof useReportFilters> }) {
  const query = useQuery({
    queryKey: ['reports', 'branch-performance', filters.params],
    queryFn: () => reportsApi.branchPerformance(filters.params),
  })

  if (query.isPending) return <SkeletonText lines={6} />
  if (query.isError) return <ErrorState message="Could not load branch performance." onRetry={() => query.refetch()} />
  if (query.data.rows.length === 0) return <EmptyState title="No branch activity" description="No sales in this date range." />

  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Branch</Table.HeaderCell>
        <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
        <Table.HeaderCell align="right">Gross sales</Table.HeaderCell>
        <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
        <Table.HeaderCell align="right">Average sale</Table.HeaderCell>
        <Table.HeaderCell align="right">Discounts</Table.HeaderCell>
        <Table.HeaderCell align="right">Returns</Table.HeaderCell>
        <Table.HeaderCell align="right">Voids</Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {query.data.rows.map((r) => (
          <Table.Row key={r.branchId}>
            <Table.Cell>{r.branchName}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.netSales)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.grossSales)}</Table.Cell>
            <Table.Cell align="right">{r.completedTransactions}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.averageTransactionValue)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.discounts)}</Table.Cell>
            <Table.Cell align="right">{r.returnsCount} ({formatMoney(r.returnsValue)})</Table.Cell>
            <Table.Cell align="right">{r.voidedSalesCount} ({formatMoney(r.voidedSalesValue)})</Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}
```

Leave `RegisterPerformanceTab`/`CashierPerformanceTab` as later additions (Tasks 6/7) — do not stub them with placeholder content; simply don't reference them until those tasks add them (i.e., temporarily inline a minimal working `BranchPerformanceTab`-only version of this file for this task's own commit, and let Tasks 6/7 extend the same file rather than you pre-declaring empty stubs now).

Verify every imported component's actual export shape (`Table.HeaderCell`'s `align` prop, `ReportFilterBar`'s actual props including whatever prop actually controls branch-filter visibility — this plan guesses `showBranchFilter`, confirm the real prop name, `Pagination`'s props even though this tab doesn't paginate) against the real files before finalizing — do not assume this plan's guesses are pixel-perfect.

- [ ] **Step 4: Register the route and nav entry**

In `web/negosio-web/src/App.tsx`, add a route for `PerformanceReportsPage` following the exact pattern of the existing `/reports`/fulfillment-reports route registration (same layout wrapper, same route-guard/policy check if one exists at the route level).

In `web/negosio-web/src/lib/nav.ts`, add a nav entry for "Performance Reports" (or whatever label fits this codebase's existing naming, e.g. matching "Reports"/"Fulfillment Reports" capitalization convention) alongside the existing Reports entries.

- [ ] **Step 5: Run `tsc -b` and `npm run build`**

Run: `cd web/negosio-web && npx tsc -b && npm run build`. Expected: zero errors.

- [ ] **Step 6: Live-verify**

Start the API and frontend, navigate to the new page, confirm the Branches tab loads real data for a seeded tenant with at least 2 branches, confirm an Owner/Admin sees every branch and a Manager sees only their own (log in as each role if seed data supports it, or note this as a Task 8 verification item if role-switching isn't convenient here).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(web): add Performance Reports page with a Branch performance tab"
```

---

### Task 6: Frontend — Register performance tab (sales aggregate + session reconciliation)

**Files:**
- Modify: `web/negosio-web/src/pages/PerformanceReportsPage.tsx`
- Modify: `web/negosio-web/src/api/reports.ts`
- Modify: `web/negosio-web/src/api/types.ts`

**Interfaces:**
- Consumes: Task 5's page shell/`Tab` type; Task 2 and Task 3's backend endpoints.
- Produces: `RegisterPerformanceTab` component, filling the slot Task 5 left for it.

Per the spec, this tab needs two sub-views at different granularities — a sales/payment/cash-movement aggregate table (day-range-aware) and a closed-session reconciliation list (session-row-aware, paged). Use a simple sub-tab or stacked-sections layout within this one tab; do not force both into one table.

- [ ] **Step 1: Mirror the backend types**

Add to `types.ts`:

```typescript
export interface RegisterPerformanceRowDto {
  registerId: string
  registerName: string
  branchId: string
  branchName: string
  grossSales: number
  netSales: number
  completedTransactions: number
  averageTransactionValue: number
  paymentMethods: PaymentMethodBreakdownDto[]
  cashIn: number
  cashOut: number
}

export interface RegisterPerformanceResultDto {
  fromUtc: string
  toUtc: string
  rows: RegisterPerformanceRowDto[]
}

export interface RegisterSessionReconciliationRowDto {
  sessionId: string
  registerId: string
  registerName: string
  branchId: string
  branchName: string
  openedAtUtc: string
  closedAtUtc: string
  openedByName: string
  closedByName: string
  openingCash: number
  closingCash: number
  expectedCash: number
  cashDifference: number
  grossCashSales: number
  voidedCashSales: number
  refundCashOut: number
  cashIn: number
  cashOut: number
}
```

(`PaymentMethodBreakdownDto` already exists — reuse it, do not redeclare.)

- [ ] **Step 2: Add the API client functions**

```typescript
registerPerformance: (params: ReportFilterParams) =>
  apiRequest<RegisterPerformanceResultDto>(`/api/reports/register-performance${qs({ ...params })}`),
registerSessions: (params: ReportFilterParams & { page: number; pageSize: number }) =>
  apiRequest<PagedResult<RegisterSessionReconciliationRowDto>>(`/api/reports/register-sessions${qs({ ...params })}`),
```

(`PagedResult<T>` should already exist as a generic TS type mirroring the backend's — confirm and reuse; if it doesn't exist as a generic yet, check how `FulfillmentReportsPage.tsx`'s paged results are typed and follow the same pattern.)

- [ ] **Step 3: Add `RegisterPerformanceTab` to `PerformanceReportsPage.tsx`**

```tsx
function RegisterPerformanceTab({ filters }: { filters: ReturnType<typeof useReportFilters> }) {
  const [view, setView] = useState<'sales' | 'sessions'>('sales')
  return (
    <div className="space-y-4">
      <div className="flex gap-2">
        <SubTabButton active={view === 'sales'} onClick={() => setView('sales')}>Sales</SubTabButton>
        <SubTabButton active={view === 'sessions'} onClick={() => setView('sessions')}>Closed sessions</SubTabButton>
      </div>
      {view === 'sales' ? <RegisterSalesView filters={filters} /> : <RegisterSessionsView filters={filters} />}
    </div>
  )
}

function SubTabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-full px-3 py-1 text-[13px] font-semibold ${active ? 'bg-primary-100 text-primary-700' : 'bg-surface-subtle text-text-secondary'}`}
    >
      {children}
    </button>
  )
}

function RegisterSalesView({ filters }: { filters: ReturnType<typeof useReportFilters> }) {
  const query = useQuery({
    queryKey: ['reports', 'register-performance', filters.params],
    queryFn: () => reportsApi.registerPerformance(filters.params),
  })

  if (query.isPending) return <SkeletonText lines={6} />
  if (query.isError) return <ErrorState message="Could not load register performance." onRetry={() => query.refetch()} />
  if (query.data.rows.length === 0) return <EmptyState title="No register activity" description="No sales in this date range." />

  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Register</Table.HeaderCell>
        <Table.HeaderCell>Branch</Table.HeaderCell>
        <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
        <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
        <Table.HeaderCell align="right">Cash in</Table.HeaderCell>
        <Table.HeaderCell align="right">Cash out</Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {query.data.rows.map((r) => (
          <Table.Row key={r.registerId}>
            <Table.Cell>{r.registerName}</Table.Cell>
            <Table.Cell>{r.branchName}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.netSales)}</Table.Cell>
            <Table.Cell align="right">{r.completedTransactions}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.cashIn)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.cashOut)}</Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}

function RegisterSessionsView({ filters }: { filters: ReturnType<typeof useReportFilters> }) {
  const [page, setPage] = useState(1)
  const pageSize = 20
  const query = useQuery({
    queryKey: ['reports', 'register-sessions', filters.params, page],
    queryFn: () => reportsApi.registerSessions({ ...filters.params, page, pageSize }),
  })

  if (query.isPending) return <SkeletonText lines={6} />
  if (query.isError) return <ErrorState message="Could not load session reconciliation." onRetry={() => query.refetch()} />
  if (query.data.items.length === 0) return <EmptyState title="No closed sessions" description="No sessions were closed in this date range." />

  return (
    <>
      <Table>
        <Table.Head>
          <Table.HeaderCell>Register</Table.HeaderCell>
          <Table.HeaderCell>Closed</Table.HeaderCell>
          <Table.HeaderCell>Closed by</Table.HeaderCell>
          <Table.HeaderCell align="right">Opening cash</Table.HeaderCell>
          <Table.HeaderCell align="right">Closing cash</Table.HeaderCell>
          <Table.HeaderCell align="right">Expected</Table.HeaderCell>
          <Table.HeaderCell align="right">Difference</Table.HeaderCell>
        </Table.Head>
        <Table.Body>
          {query.data.items.map((r) => (
            <Table.Row key={r.sessionId}>
              <Table.Cell>{r.registerName}</Table.Cell>
              <Table.Cell>{new Date(r.closedAtUtc).toLocaleString()}</Table.Cell>
              <Table.Cell>{r.closedByName}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.openingCash)}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.closingCash)}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.expectedCash)}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.cashDifference)}</Table.Cell>
            </Table.Row>
          ))}
        </Table.Body>
      </Table>
      <Pagination
        page={query.data.page}
        pageSize={query.data.pageSize}
        totalCount={query.data.totalCount}
        totalPages={query.data.totalPages}
        onPageChange={setPage}
      />
    </>
  )
}
```

Add `import { useState } from 'react'` to this file's imports if not already present. Verify `Pagination`'s exact prop names against the real component (used elsewhere, e.g. `FulfillmentReportsPage.tsx`) before finalizing.

- [ ] **Step 4: Run `tsc -b` and `npm run build`**

- [ ] **Step 5: Live-verify**

Confirm the Sales sub-view shows correct per-register figures, the Closed sessions sub-view shows real closed sessions with reconciliation figures matching what `RegisterSessionService` actually persisted at close time (cross-check one session's numbers against what you saw when closing it), and that an open session never appears in the Closed sessions list.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): add Register performance tab (sales aggregate + closed-session reconciliation)"
```

---

### Task 7: Frontend — Cashier performance tab

**Files:**
- Modify: `web/negosio-web/src/pages/PerformanceReportsPage.tsx`
- Modify: `web/negosio-web/src/api/reports.ts`
- Modify: `web/negosio-web/src/api/types.ts`

**Interfaces:**
- Consumes: Task 5's page shell; Task 4's backend endpoint.
- Produces: `CashierPerformanceTab`, filling the last slot in `PerformanceReportsPage.tsx`.

- [ ] **Step 1: Mirror the backend types**

```typescript
export interface CashierPerformanceRowDto {
  cashierUserId: string
  cashierName: string
  grossSales: number
  netSales: number
  completedTransactions: number
  averageTransactionValue: number
  discounts: number
  returnsCount: number
  returnsValue: number
  voidedSalesCount: number
  voidedSalesValue: number
}

export interface CashierPerformanceResultDto {
  fromUtc: string
  toUtc: string
  rows: CashierPerformanceRowDto[]
}
```

- [ ] **Step 2: Add the API client function**

```typescript
cashierPerformance: (params: ReportFilterParams) =>
  apiRequest<CashierPerformanceResultDto>(`/api/reports/cashier-performance${qs({ ...params })}`),
```

- [ ] **Step 3: Add `CashierPerformanceTab`**

```tsx
function CashierPerformanceTab({ filters }: { filters: ReturnType<typeof useReportFilters> }) {
  const query = useQuery({
    queryKey: ['reports', 'cashier-performance', filters.params],
    queryFn: () => reportsApi.cashierPerformance(filters.params),
  })

  if (query.isPending) return <SkeletonText lines={6} />
  if (query.isError) return <ErrorState message="Could not load cashier performance." onRetry={() => query.refetch()} />
  if (query.data.rows.length === 0) return <EmptyState title="No cashier activity" description="No sales in this date range." />

  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Cashier</Table.HeaderCell>
        <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
        <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
        <Table.HeaderCell align="right">Average sale</Table.HeaderCell>
        <Table.HeaderCell align="right">Discounts</Table.HeaderCell>
        <Table.HeaderCell align="right">Returns</Table.HeaderCell>
        <Table.HeaderCell align="right">Voids</Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {query.data.rows.map((r) => (
          <Table.Row key={r.cashierUserId}>
            <Table.Cell>{r.cashierName}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.netSales)}</Table.Cell>
            <Table.Cell align="right">{r.completedTransactions}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.averageTransactionValue)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.discounts)}</Table.Cell>
            <Table.Cell align="right">{r.returnsCount} ({formatMoney(r.returnsValue)})</Table.Cell>
            <Table.Cell align="right">{r.voidedSalesCount} ({formatMoney(r.voidedSalesValue)})</Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}
```

Note: no approver column for discounts here, per the spec's explicit instruction not to fabricate that attribution.

- [ ] **Step 4: Run `tsc -b` and `npm run build`**

- [ ] **Step 5: Live-verify**

Confirm a Manager or Owner who also rang up a sale appears in this list alongside actual Cashiers (per Task 4's design — this is the one behavior most likely to look "wrong" at a glance if someone expects a strict Role filter, so specifically check it).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): add Cashier performance tab"
```

---

### Task 8: Final verification for Reports Phase C

**Files:** none unless a defect is found.

- [ ] **Step 1: Full backend suite**

Run: `dotnet test`. Record the exact pass count.

- [ ] **Step 2: Full frontend build**

Run: `cd web/negosio-web && npx tsc -b && npm run build`. Expected: zero errors.

- [ ] **Step 3: Run every verification scenario from the spec's Part 3**

For each of: split tenders, discounts (confirm no fabricated approver), partial/full returns, voids, cash movements, a session crossing midnight, multiple branches (Owner/Admin sees all, Manager forced to their own — attempt to override as Manager and confirm the server ignores it), and the reframed cashier-branch-reassignment scenario — either point at the specific automated test from Tasks 1-4 that already covers it, or drive it live through the running app and record what you observed. Do not skip any scenario silently; if one genuinely cannot be verified (e.g., no convenient way to seed a midnight-crossing session), say so explicitly rather than omitting it from the report.

- [ ] **Step 4: Cross-check Phase C's totals against Overview**

For the same date range and the same effective branch scope, confirm the sum of Branch performance's `NetSales` across every branch equals Overview's tenant-wide Net sales figure (Owner/Admin view). This is the strongest end-to-end correctness check available, since Overview is already trusted and this plan's queries are meant to be the same computation, just grouped differently.

- [ ] **Step 5: Write a short summary**

Do not merge, do not push. Report: exact test counts, every verification scenario's result, the exact metric definitions actually implemented (confirm they match the spec, or note any deviation and why), and any remaining limitations (the discount-approver gap, and anything else discovered during implementation).
