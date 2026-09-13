# POS Delivery Charge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persisted, server-validated `DeliveryCharge` to the POS checkout flow — a numeric PHP-currency field inside the existing "For delivery" section of the Take-payment modal — so a delivery sale's grand total, cash/change math, and every receipt/detail view correctly include the fee, with `0` meaning free delivery.

**Architecture:** `DeliveryCharge` is a first-class column on `Sale` (server-computed into `GrandTotal`, never trusted from the client beyond the raw fee amount) and a snapshot column on `DeliveryReceipt` (for reprint stability, mirroring the existing snapshot fields). The frontend keeps the charge as controlled state one level up in `PosTerminal` (same reasoning as the existing `deliveryFields` state: it must survive a failed-payment modal close/reopen) and folds it into the same `effectiveAmountDue` the payment modal already uses for change math.

**Tech Stack:** .NET 9 / ASP.NET Core / EF Core 9 / SQL Server (LocalDB) / FluentValidation / xUnit + FluentAssertions, on the backend; React 19 / TypeScript (strict) / Vite / TanStack Query on the frontend (no frontend test runner is configured in this repo — frontend tasks verify with `npx tsc --noEmit` + `npm run build` plus a live manual pass, matching the existing project convention).

**Spec:** This plan implements the delivery-charge requirements given directly in conversation (no separate spec file) — see the plan header's "Global Constraints" below for the load-bearing rules, and `docs/handover.md` for the pre-existing "For delivery" feature this extends (branch `feature/pos-for-delivery`, commits `5b654b0`/`8181dae`/`ded7538`).

## Global Constraints

- Backend is authoritative for the total: `GrandTotal = (Subtotal − DiscountTotal [+ TaxTotal if tax-exclusive]) + DeliveryCharge`, computed server-side in `CheckoutService`; the client never sends a total, only the raw `DeliveryCharge`.
- `DeliveryCharge` on `Sale` is `decimal(18,2)`, `NOT NULL`, defaults to `0` — a non-delivery sale always has `DeliveryCharge == 0`.
- No `Sale.IsForDelivery` flag — a `DeliveryReceipt`'s existence remains the only "this was a delivery sale" signal, exactly as before this feature.
- No Delivery Receipt document number — unchanged from the existing design.
- Minimum `0`, no negative values, at most 2 decimal places — enforced identically on frontend and backend. No upper cap: the codebase has no precedent for one on any monetary input (`CostPrice`/`SellingPrice` in `ProductValidators.cs`, `OpeningCash`/`ClosingCash` in `RegisterValidators.cs` are all `GreaterThanOrEqualTo(0)` only, no maximum) — this plan doesn't introduce one either, for consistency.
- `web/negosio-web/src/pages/SalesPage.tsx` (transaction history) and `PosCompletePage.tsx` need **no changes** — both already display `grandTotal` straight from the backend (`SaleSummaryDto.grandTotal` / `SaleResultDto.grandTotal`), which is already inclusive of the delivery charge once Task 2 lands. Listed here so this isn't mistaken for a coverage gap.
- `DeliveryCharge` lives **only** on `Sale` — never duplicated onto `DeliveryReceipt`. `Sale` is the financial source of truth (the charge affects amount paid and final total); `DeliveryReceipt` stays purely operational (`SaleId`, recipient/address/contact/notes). Anything that needs a delivery's charge (the delivery receipt view, the delivery report) reads it live through `DeliveryReceipt.SaleId → Sale.Id`, never a second stored copy.
- A `DeliveryReceipt` creation failure must never roll back or block the already-completed `Sale` (unchanged existing guarantee) — and the `DeliveryCharge` already persisted on that `Sale` must survive such a failure untouched.
- The new Delivery Report lives in the existing Reports module (`ReportsController`/`IReportsService`), gated behind `AuthorizationPolicies.ReportsView` / `useCan('reports:view')` — Owner/Admin/Manager only, identical to every other report (stricter than Sales, which also allows Cashier). Do not add a new capability, policy, or controller for it. It sources rows from `DeliveryReceipt` joined to `Sale` — never a new `Deliveries` table, never a duplicated `SaleNumber`/`DeliveryCharge` column.
- Retrying checkout with the same `ClientRequestId`, or retrying delivery-receipt creation for the same sale, must never apply or persist the delivery charge more than once (both idempotency paths already exist for the rest of the sale/DR — this plan does not change their mechanics, only feeds the charge through them).
- Do not merge, do not push — stay on `feature/pos-for-delivery`. Commit after every green step.

---

## Backend

### Task 1: `Sale` entity + EF configuration carry `DeliveryCharge`

**Files:**
- Modify: `src/Negosio.Domain/Entities/Sales/Sale.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs`
- Modify: `tests/Negosio.UnitTests/Sales/SaleVoidTests.cs:13` (existing `sale.Complete(...)` call — signature is changing)
- Modify: `tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs:74` (same)
- Test: `tests/Negosio.UnitTests/Sales/SaleDeliveryChargeTests.cs` (new)

**Interfaces:**
- Produces: `Sale.DeliveryCharge` (`decimal`, public getter, private setter) and the new `Sale.Complete(decimal subtotal, decimal discountTotal, decimal taxTotal, decimal deliveryCharge, decimal grandTotal, decimal amountPaid, decimal changeDue)` signature — every later task that calls `Complete` or reads `sale.DeliveryCharge` depends on this exact param order and name.

- [ ] **Step 1: Write the failing test**

Create `tests/Negosio.UnitTests/Sales/SaleDeliveryChargeTests.cs`:

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Sales;

public class SaleDeliveryChargeTests
{
    private static Sale MakeUncompletedSale()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());
        sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 2m, DiscountType.None, 0m, 150m, 0m, 0m, 150m, 40m);
        return sale;
    }

    [Fact]
    public void Complete_sets_delivery_charge_and_it_is_reflected_in_grand_total()
    {
        var sale = MakeUncompletedSale();

        sale.Complete(subtotal: 150m, discountTotal: 0m, taxTotal: 0m, deliveryCharge: 60m, grandTotal: 210m, amountPaid: 210m, changeDue: 0m);

        sale.DeliveryCharge.Should().Be(60m);
        sale.GrandTotal.Should().Be(210m);
    }

    [Fact]
    public void Complete_defaults_delivery_charge_to_zero_for_a_normal_sale()
    {
        var sale = MakeUncompletedSale();

        sale.Complete(subtotal: 150m, discountTotal: 0m, taxTotal: 0m, deliveryCharge: 0m, grandTotal: 150m, amountPaid: 150m, changeDue: 0m);

        sale.DeliveryCharge.Should().Be(0m);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Negosio.UnitTests --filter SaleDeliveryChargeTests`
Expected: FAIL to compile — `Sale` has no `DeliveryCharge` member and `Complete` has no 7-argument overload.

- [ ] **Step 3: Implement `Sale.DeliveryCharge` and the new `Complete` signature**

In `src/Negosio.Domain/Entities/Sales/Sale.cs`, add the property right after `TaxTotal` (line 54) so declaration order matches the DTO order used everywhere else in this plan:

```csharp
    public decimal TaxTotal { get; private set; }

    public decimal DeliveryCharge { get; private set; }

    public decimal GrandTotal { get; private set; }
```

Replace the existing `Complete` method (line 133):

```csharp
    public void Complete(decimal subtotal, decimal discountTotal, decimal taxTotal, decimal deliveryCharge, decimal grandTotal, decimal amountPaid, decimal changeDue)
    {
        Subtotal = subtotal;
        DiscountTotal = discountTotal;
        TaxTotal = taxTotal;
        DeliveryCharge = deliveryCharge;
        GrandTotal = grandTotal;
        AmountPaid = amountPaid;
        ChangeDue = changeDue;
        Status = SaleStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
    }
```

In `src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs`, add right after the `TaxTotal` line (line 24):

```csharp
        builder.Property(s => s.TaxTotal).HasPrecision(18, 2);
        builder.Property(s => s.DeliveryCharge).HasPrecision(18, 2).HasDefaultValue(0m);
        builder.Property(s => s.GrandTotal).HasPrecision(18, 2);
```

- [ ] **Step 4: Fix the two other unit tests broken by the signature change**

In `tests/Negosio.UnitTests/Sales/SaleVoidTests.cs:13`, change:
```csharp
        sale.Complete(150m, 0m, 0m, 150m, 150m, 0m);
```
to:
```csharp
        sale.Complete(150m, 0m, 0m, 0m, 150m, 150m, 0m);
```

In `tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs:74`, make the identical change (same six-argument call, same fix — insert a `0m` as the 4th argument).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.UnitTests --filter "SaleDeliveryChargeTests|SaleVoidTests|SaleAggregateTests"`
Expected: PASS (all green, including the two pre-existing files you just fixed).

- [ ] **Step 6: Commit**

```bash
git add src/Negosio.Domain/Entities/Sales/Sale.cs src/Negosio.Infrastructure/Persistence/Configurations/SaleConfiguration.cs tests/Negosio.UnitTests/Sales/SaleVoidTests.cs tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs tests/Negosio.UnitTests/Sales/SaleDeliveryChargeTests.cs
git commit -m "feat(sales): add DeliveryCharge to the Sale entity"
```

---

### Task 2: Checkout accepts, validates, and totals the delivery charge

**A circular-dependency note, so you don't chase a phantom bug:** this task's code changes are what fix the compile error Task 1 left behind (`CheckoutService.cs` still called the old 6-arg `Sale.Complete(...)`) — until you finish Step 4 below, `Negosio.Application`/`Negosio.Api` will not build at all, for that pre-existing, expected reason. Separately, this task's own integration tests need the `Sales.DeliveryCharge` **column** to physically exist in the real LocalDB tenant database — and that only happens in **Task 3** (the migration), which comes right after this one specifically because migration generation itself needs `Negosio.Api` to build, which needs *this* task's fix in place first. So: get the code changes below to compile cleanly (zero build errors across the whole solution) as this task's actual verification gate — do not expect `dotnet test` to go green here. Task 3's last step comes back and runs this task's tests for the real, final GREEN confirmation once the schema catches up.

**Files:**
- Modify: `src/Negosio.Application/Pos/CheckoutContracts.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutValidator.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutService.cs`
- Test: `tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryChargeTests.cs` (new)

**Interfaces:**
- Consumes: `Sale.Complete(subtotal, discountTotal, taxTotal, deliveryCharge, grandTotal, amountPaid, changeDue)` from Task 1.
- Produces: `CheckoutRequest.DeliveryCharge` (`decimal`, default `0m`) and `SaleResultDto.DeliveryCharge` (`decimal`) — every later task and the entire frontend depend on these exact names. In particular, **Task 4's own test-arrangement helper calls `new CheckoutRequest(..., DeliveryCharge: ...)`**, and **Task 3's migration-generation command itself cannot run until this task's fix to `CheckoutService.cs` lets `Negosio.Api` build again** — both are why this task now runs immediately after Task 1, before either of them (a pre-flight-scan miss caught mid-execution, see the ledger).

- [ ] **Step 1: Write the failing tests**

Create `tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryChargeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

public class CheckoutDeliveryChargeTests : IntegrationTest
{
    public CheckoutDeliveryChargeTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record Scene(Guid BranchId, Guid SessionId, Guid VariantId);

    private async Task<Scene> ArrangeAsync(decimal price = 100m, decimal stock = 20m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);
        return new Scene(branchId, session.Id, variantId);
    }

    private static CheckoutRequest DeliverySale(Scene s, decimal quantity, decimal cashReceived, decimal deliveryCharge) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, null) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) },
        DeliveryCharge: deliveryCharge);

    [Fact]
    public async Task Normal_sale_without_a_delivery_charge_defaults_to_zero_and_is_unaffected()
    {
        var scene = await ArrangeAsync(price: 100m);

        var result = await CheckoutOkAsync(DeliverySale(scene, 1m, 100m, 0m));

        result.DeliveryCharge.Should().Be(0m);
        result.GrandTotal.Should().Be(100m);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync()).Should().Be(0);
            return true;
        });
    }

    [Fact]
    public async Task Positive_delivery_charge_is_added_to_the_grand_total_exactly_once()
    {
        var scene = await ArrangeAsync(price: 100m);

        var result = await CheckoutOkAsync(DeliverySale(scene, 1m, 160m, deliveryCharge: 60m));

        result.DeliveryCharge.Should().Be(60m);
        result.GrandTotal.Should().Be(160m);
        result.AmountPaid.Should().Be(160m);
        result.ChangeDue.Should().Be(0m);

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.SingleAsync();
            sale.DeliveryCharge.Should().Be(60m);
            return true;
        });
    }

    [Fact]
    public async Task Free_delivery_keeps_the_charge_at_zero_and_leaves_the_total_unchanged()
    {
        var scene = await ArrangeAsync(price: 75m);

        var result = await CheckoutOkAsync(DeliverySale(scene, 1m, 75m, deliveryCharge: 0m));

        result.DeliveryCharge.Should().Be(0m);
        result.GrandTotal.Should().Be(75m);
    }

    [Fact]
    public async Task Negative_delivery_charge_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(DeliverySale(scene, 1m, 200m, deliveryCharge: -10m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delivery_charge_with_more_than_two_decimal_places_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(DeliverySale(scene, 1m, 200m, deliveryCharge: 10.005m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retrying_the_same_client_request_never_applies_the_delivery_charge_twice()
    {
        var scene = await ArrangeAsync(price: 100m);
        var request = DeliverySale(scene, 1m, 160m, deliveryCharge: 60m);

        var first = await CheckoutOkAsync(request);
        var second = await CheckoutOkAsync(request); // same ClientRequestId

        second.SaleId.Should().Be(first.SaleId);
        second.WasExistingRequest.Should().BeTrue();
        second.DeliveryCharge.Should().Be(60m);
        second.GrandTotal.Should().Be(160m);

        await InScopeAsync(async db =>
        {
            (await db.Sales.CountAsync()).Should().Be(1);
            return true;
        });
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter CheckoutDeliveryChargeTests`
Expected: FAIL to compile — `CheckoutRequest` has no `DeliveryCharge` parameter and `SaleResultDto` has no `DeliveryCharge` member.

- [ ] **Step 3: Implement the contract + validator changes**

In `src/Negosio.Application/Pos/CheckoutContracts.cs`, replace the `CheckoutRequest` and `SaleResultDto` records:

```csharp
public sealed record CheckoutRequest(
    Guid BranchId,
    Guid RegisterSessionId,
    Guid ClientRequestId,
    IReadOnlyList<CheckoutItemInput> Items,
    IReadOnlyList<CheckoutPaymentInput> Payments,
    /// <summary>The delivery fee for a "for delivery" sale, 0 for a normal sale. Added to the
    /// server-computed sale total to form <see cref="SaleResultDto.GrandTotal"/> — the client never
    /// sends a total, only this raw fee.</summary>
    decimal DeliveryCharge = 0m,
    /// <summary>Manager/Admin/Owner approval — only used when a Cashier without the DiscountApply
    /// grant submits a sale that carries any line discount. Reuses Void's approval shape.</summary>
    VoidSaleApprovalInput? Approval = null);

public sealed record SaleResultDto(
    Guid SaleId,
    string SaleNumber,
    SaleStatus Status,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal DeliveryCharge,
    decimal GrandTotal,
    decimal AmountPaid,
    decimal ChangeDue,
    bool WasExistingRequest);
```

In `src/Negosio.Application/Pos/CheckoutValidator.cs`, add after the `Payments` rule:

```csharp
        RuleFor(x => x.Payments).NotEmpty().WithMessage("At least one payment is required.");
        RuleFor(x => x.DeliveryCharge)
            .GreaterThanOrEqualTo(0m).WithMessage("Delivery charge cannot be negative.")
            .Must(v => v == Math.Round(v, 2, MidpointRounding.AwayFromZero))
            .WithMessage("Delivery charge can have at most 2 decimal places.");
```

- [ ] **Step 4: Implement the `CheckoutService` calculation change**

In `src/Negosio.Application/Pos/CheckoutService.cs`, replace lines 153-161:

```csharp
        var subtotal = Money.Round(lines.Sum(l => l.Amounts.Gross));
        var discountTotal = Money.Round(lines.Sum(l => l.Amounts.Discount));
        var taxTotal = Money.Round(lines.Sum(l => l.Amounts.Tax));
        var deliveryCharge = Money.Round(request.DeliveryCharge);
        var saleTotal = tenant.PricesIncludeTax
            ? subtotal - discountTotal
            : subtotal - discountTotal + taxTotal;
        var grandTotal = saleTotal + deliveryCharge;

        // 5. Resolve payments against the grand total (sale total + delivery charge).
        var payments = ResolvePayments(request.Payments, grandTotal);
```

Replace line 182:

```csharp
        sale.Complete(subtotal, discountTotal, taxTotal, deliveryCharge, grandTotal, payments.Sum(p => p.Amount), Money.Round(payments.Sum(p => p.ChangeAmount ?? 0m)));
```

Replace `ToResult` (lines 298-300):

```csharp
    private static SaleResultDto ToResult(Sale sale, bool wasExisting) => new(
        sale.Id, sale.SaleNumber, sale.Status, sale.Subtotal, sale.DiscountTotal, sale.TaxTotal,
        sale.DeliveryCharge, sale.GrandTotal, sale.AmountPaid, sale.ChangeDue, wasExisting);
```

- [ ] **Step 5: Confirm the whole solution builds cleanly, then attempt the tests**

Run: `dotnet build Negosio.sln`
Expected: **0 errors** — this is the real verification gate for this task. The pre-existing `CheckoutService.cs` error from Task 1 should now be gone, because Step 4 above just fixed the exact line that caused it.

Then run: `dotnet test tests/Negosio.IntegrationTests --filter "CheckoutDeliveryChargeTests|CheckoutTests|CheckoutIdempotencyTests|CheckoutConcurrencyTests"`
Expected: the build succeeds (proving the code compiles), but the tests themselves are expected to FAIL at runtime with a SQL error naming an unknown `DeliveryCharge` column — the `Sales` table in the real LocalDB tenant database doesn't have that column yet (Task 3, next, generates and applies the migration for it). Capture this output as-is in your report; do not try to make it pass by any other means (no manual `ALTER TABLE`, no skipping the test) — Task 3 closes this gap for you as its own final step.

- [ ] **Step 6: Commit**

```bash
git add src/Negosio.Application/Pos/CheckoutContracts.cs src/Negosio.Application/Pos/CheckoutValidator.cs src/Negosio.Application/Pos/CheckoutService.cs tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryChargeTests.cs
git commit -m "feat(pos): checkout accepts, validates, and totals a delivery charge"
```

---

### Task 3: EF Core migration for `Sale.DeliveryCharge`

Task 2 (just before this one) intentionally left its own integration tests unable to run to completion — the `Sales.DeliveryCharge` column didn't exist yet in the real LocalDB tenant database. This task closes that loop: it generates and applies the migration, then goes back and runs Task 2's tests for the first real GREEN confirmation of both tasks together. (Migration generation could not run any earlier than this, either — `dotnet-ef` needs to build the startup project `src/Negosio.Api`, which was broken by Task 1's signature change until Task 2 fixed it moments ago.)

**Files:**
- Create: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/<timestamp>_AddSaleDeliveryCharge.cs` (+ matching `.Designer.cs`)
- Modify: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/TenantDbContextModelSnapshot.cs` (auto-updated by the command below — do not hand-edit)

**Interfaces:**
- Consumes: the `HasPrecision(18, 2).HasDefaultValue(0m)` configuration on `Sale.DeliveryCharge` from Task 1 — this is what EF diffs against to generate the migration. `DeliveryReceipt` gets no schema change (the join-based DR read happens in Task 4, a read-side-only change). Also consumes Task 2's now-fixed `Negosio.Api` build (a prerequisite for `dotnet-ef` to run at all).
- Produces: the `Sales.DeliveryCharge` column in the tenant schema, applied to every tenant database. Task 2's own tests (deferred to this task's Step 5), and Task 4, both depend on this column existing.

- [ ] **Step 1: Ensure the EF tool is restored**

Run: `dotnet tool restore`

- [ ] **Step 2: Generate the migration**

Run (exact command and flags per `README.md` and `docs/adr/0012-split-migration-histories.md` — the `--namespace`/`--output-dir` are mandatory, not derived automatically):

```bash
dotnet dotnet-ef migrations add AddSaleDeliveryCharge --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
```

If this command itself fails to build, stop and check `dotnet build Negosio.sln` first — Task 2 should have already fixed the one known compile error; if a different one appears, that's a real problem to report, not something to work around here.

- [ ] **Step 3: Inspect the generated `Up()`**

Open the new migration file and confirm it contains exactly one `AddColumn<decimal>` call, on the `Sales` table, with `type: "decimal(18,2)"`, `precision: 18`, `scale: 2`, `nullable: false`, and **`defaultValue: 0m`** (the `HasDefaultValue(0m)` from Task 1 is what makes EF emit this — without it, applying the migration to a tenant database that already has rows would fail with a NOT NULL constraint violation, and any existing sale would be left without the required delivery charge of `0`). If it's missing `defaultValue: 0m`, add it by hand before proceeding — do not apply a migration that lacks it. Confirm `DeliveryReceipts` is untouched by this migration.

- [ ] **Step 4: Apply the migration to the local dev tenant database and verify the app still starts**

Run: `dotnet dotnet-ef database update --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api`
Expected: succeeds with no errors, reports the new migration applied. Confirm an existing (pre-migration) sale row now reads `DeliveryCharge = 0`.

- [ ] **Step 5: Close the loop — run Task 2's tests for real**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "CheckoutDeliveryChargeTests|CheckoutTests|CheckoutIdempotencyTests|CheckoutConcurrencyTests"`
Expected: PASS — now that the schema matches the code, this is the genuine GREEN confirmation for both Task 2's checkout changes and this task's migration, proving the default-`0m` change is fully backward compatible.

- [ ] **Step 6: Commit**

```bash
git add src/Negosio.Infrastructure/Persistence/Migrations/Tenant/
git commit -m "feat(db): migration for Sale.DeliveryCharge"
```

---

### Task 4: `DeliveryReceiptDto` surfaces the linked sale's delivery charge (no duplicate storage)

`DeliveryReceipt` gets **no new column and no constructor change** — per the agreed design, `Sale` is the sole source of truth for the charge. This task only teaches the read side (`DeliveryReceiptService.GetAsync`) to look it up through the existing `SaleId` relationship, and proves the charge survives a rejected delivery-receipt attempt.

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs` (extend)

**Interfaces:**
- Consumes: `sale.DeliveryCharge` from Task 1, read live via a lookup keyed on `DeliveryReceipt.SaleId` — never stored a second time. Also consumes `CheckoutRequest.DeliveryCharge` from Task 2 — this task's own test-arrangement helper needs a way to put a non-zero charge onto a real sale through the public checkout API, which didn't exist before Task 2.
- Produces: `DeliveryReceiptDto.DeliveryCharge` (`decimal`) — Task 12's `DeliveryReceiptPage` reads this exactly as if it were a stored field; it doesn't know or care that it's a live join.

- [ ] **Step 1: Write the failing tests**

In `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs`, first extend the existing `ArrangeSaleAsync` helper (lines 25-46) to accept an optional delivery charge — replace it with:

```csharp
    private async Task<DrScene> ArrangeSaleAsync(decimal qty = 1m, decimal price = 100m, decimal deliveryCharge = 0m)
    {
        if (CurrentTenantId == Guid.Empty)
        {
            await RegisterLoginAndAuthorizeAsync();
        }

        var branchId = await InScopeAsync(db => db.Branches
            .OrderBy(b => b.CreatedAtUtc).Select(b => b.Id).FirstAsync());
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (productId, variantId) = await SeedStockedProductAsync(
            branchId, category.Id, sellingPrice: price, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + deliveryCharge + 100m) },
            DeliveryCharge: deliveryCharge));

        return new DrScene(sale.SaleId, sale.SaleNumber, productId);
    }
```

(This is backward compatible — every existing call like `ArrangeSaleAsync(qty: 2m, price: 50m)` still works, `deliveryCharge` just defaults to `0m`.)

Then add two new facts to the same class:

```csharp
    [Fact]
    public async Task Delivery_receipt_reflects_the_linked_sales_delivery_charge()
    {
        var scene = await ArrangeSaleAsync(price: 100m, deliveryCharge: 60m);

        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req()))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        dr.DeliveryCharge.Should().Be(60m);
    }

    [Fact]
    public async Task A_rejected_delivery_receipt_attempt_leaves_the_completed_sale_and_its_charge_intact()
    {
        var scene = await ArrangeSaleAsync(price: 100m, deliveryCharge: 60m);

        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt",
            new CreateDeliveryReceiptRequest("  ", "123 Ayala Ave", null, null, null)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InScopeAsync(async db =>
        {
            var sale = await db.Sales.SingleAsync(s => s.Id == scene.SaleId);
            sale.Status.Should().Be(SaleStatus.Completed);
            sale.DeliveryCharge.Should().Be(60m);
            return true;
        });

        var retried = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipt", Req());
        retried.StatusCode.Should().Be(HttpStatusCode.Created);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptTests`
Expected: The two new tests FAIL to compile (`DeliveryReceiptDto` has no `DeliveryCharge` member) — every other test in the file should still be unaffected by the `ArrangeSaleAsync` signature change alone (default param).

- [ ] **Step 3: Implement**

In `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`, replace `DeliveryReceiptDto`:

```csharp
public sealed record DeliveryReceiptDto(
    Guid Id,
    DateTime CreatedAtUtc,
    string? RelatedSaleNumber,
    string BranchName,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    string PreparedByName,
    IReadOnlyList<DeliveryReceiptItemDto> Items,
    /// <summary>Read live from the linked Sale (never a stored copy on this entity) — see the plan's
    /// Global Constraints. 0 for a delivery receipt with no linked sale (there is no charge to show).</summary>
    decimal DeliveryCharge,
    string? HeaderText,
    string? FooterText,
    string BusinessName,
    string? BusinessAddress,
    string? BusinessContactNumber,
    string? TaxId,
    bool ShowPrices,
    bool ShowRelatedSaleNumber,
    bool ShowContactNumber,
    bool ShowSignatureFields);
```

In `src/Negosio.Application/Delivery/DeliveryReceiptService.cs`, in `GetAsync`, add the lookup right after the `branch` query (after line 142's `var branch = ...FirstOrDefaultAsync(ct);` block) and thread it into the returned DTO:

```csharp
        var deliveryCharge = dr.SaleId is { } saleId
            ? await _db.Sales.AsNoTracking().Where(s => s.Id == saleId).Select(s => s.DeliveryCharge).FirstOrDefaultAsync(ct)
            : 0m;
```

Then in the `return new DeliveryReceiptDto(...)` (lines 164-184), insert `deliveryCharge,` right after `items,`:

```csharp
        return new DeliveryReceiptDto(
            dr.Id,
            dr.CreatedAtUtc,
            dr.RelatedSaleNumber,
            branch?.Name ?? string.Empty,
            dr.RecipientName,
            dr.DeliveryAddress,
            dr.ContactNumber,
            dr.DeliveryNotes,
            dr.PreparedByNameSnapshot,
            items,
            deliveryCharge,
            HeaderText: settings.DeliveryHeaderText,
            FooterText: settings.DeliveryFooterText,
            BusinessName: profile.Name,
            BusinessAddress: businessAddress,
            BusinessContactNumber: branch?.ContactNumber ?? profile.ContactNumber,
            TaxId: profile.TaxId,
            ShowPrices: settings.DeliveryShowPrices,
            ShowRelatedSaleNumber: settings.DeliveryShowRelatedSaleNumber,
            ShowContactNumber: settings.DeliveryShowContactNumber,
            ShowSignatureFields: settings.DeliveryShowSignatureFields);
```

`CreateOrGetForSaleAsync` needs **no changes at all** — it never wrote a charge onto the entity, and still doesn't.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptTests`
Expected: PASS — all tests in the file, old and new.

- [ ] **Step 5: Commit**

```bash
git add src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs src/Negosio.Application/Delivery/DeliveryReceiptService.cs tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs
git commit -m "feat(delivery): surface the linked sale's delivery charge on the delivery receipt"
```

---

### Task 5: Sale detail + printed sales receipt surface the delivery charge

**Files:**
- Modify: `src/Negosio.Application/Sales/SaleContracts.cs`
- Modify: `src/Negosio.Application/Sales/SaleQueryService.cs`
- Modify: `src/Negosio.Application/Sales/ReceiptService.cs`
- Test: `tests/Negosio.IntegrationTests/Sales/SaleQueryTests.cs` (extend)
- Test: `tests/Negosio.IntegrationTests/Sales/ReceiptTests.cs` (extend)

**Interfaces:**
- Consumes: `sale.DeliveryCharge` from Task 1, `SaleResultDto`/checkout wiring from Task 2 (only indirectly, via `CheckoutOkAsync` in the new tests).
- Produces: `SaleDetailDto.DeliveryCharge` and `ReceiptDto.DeliveryCharge` — the frontend types in Task 7 mirror these exactly.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Negosio.IntegrationTests/Sales/SaleQueryTests.cs` (inside the `SaleQueryTests` class):

```csharp
    [Fact]
    public async Task Detail_reflects_the_delivery_charge()
    {
        var (branchId, sessionId, variantId) = await ArrangeAsync();
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, sessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 100m) },
            DeliveryCharge: 20m));

        var detail = await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{sale.SaleId}", TestJson.Options);

        detail!.DeliveryCharge.Should().Be(20m);
        detail.Sale.GrandTotal.Should().Be(60m); // 40 (price) + 20 delivery
    }
```

Append to `tests/Negosio.IntegrationTests/Sales/ReceiptTests.cs` (inside the `ReceiptTests` class):

```csharp
    [Fact]
    public async Task Receipt_includes_the_delivery_charge_in_the_grand_total()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) },
            DeliveryCharge: 60m));

        var receipt = await Client.GetFromJsonAsync<ReceiptDto>($"/api/sales/{sale.SaleId}/receipt", TestJson.Options);

        receipt!.DeliveryCharge.Should().Be(60m);
        receipt.GrandTotal.Should().Be(160m);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "Detail_reflects_the_delivery_charge|Receipt_includes_the_delivery_charge_in_the_grand_total"`
Expected: FAIL to compile — `SaleDetailDto`/`ReceiptDto` have no `DeliveryCharge` member.

- [ ] **Step 3: Implement**

In `src/Negosio.Application/Sales/SaleContracts.cs`, replace `SaleDetailDto`:

```csharp
public sealed record SaleDetailDto(
    SaleSummaryDto Sale,
    Guid RegisterSessionId,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal DeliveryCharge,
    decimal AmountPaid,
    decimal ChangeDue,
    DateTime? CompletedAtUtc,
    IReadOnlyList<SaleItemDto> Items,
    IReadOnlyList<SalePaymentDto> Payments,
    IReadOnlyList<SaleReturnDto> Returns,
    Guid? VoidedByUserId,
    string? VoidedByName,
    Guid? ApprovedByUserId,
    string? ApprovedByName,
    string? VoidReason,
    DateTime? VoidedAtUtc,
    bool CanVoid,
    string? VoidIneligibilityCode);
```

Replace `ReceiptDto`:

```csharp
public sealed record ReceiptDto(
    string StoreName,
    string BranchName,
    string RegisterName,
    string SaleNumber,
    string CashierName,
    DateTime CreatedAtUtc,
    IReadOnlyList<ReceiptLineDto> Lines,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal DeliveryCharge,
    decimal GrandTotal,
    IReadOnlyList<ReceiptPaymentDto> Payments,
    decimal ChangeDue,
    SaleStatus Status,
    // --- appended for Receipt Settings (Task 8) ---
    string? HeaderText,
    string? FooterText,
    string? BusinessAddress,
    string? BusinessContactNumber,
    string? TaxId,
    bool ShowBranch,
    bool ShowCashier,
    bool ShowPaymentMethod,
    bool ShowTaxLine,
    bool ShowReferenceNumber,
    ReceiptWidth Width);
```

In `src/Negosio.Application/Sales/SaleQueryService.cs`, replace the final `return` in `GetAsync` (lines 153-157):

```csharp
        return new SaleDetailDto(
            summary, sale.RegisterSessionId, sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.DeliveryCharge,
            sale.AmountPaid, sale.ChangeDue, sale.CompletedAtUtc, items, payments, returns,
            sale.VoidedByUserId, voidedByName, sale.ApprovedByUserId, approvedByName,
            sale.VoidReason, sale.VoidedAtUtc, eligibility.CanVoid, eligibility.IneligibilityCode);
```

In `src/Negosio.Application/Sales/ReceiptService.cs`, replace the `return` in `GetReceiptAsync` (lines 75-90):

```csharp
        return new ReceiptDto(
            profile.Name, branchName, registerName, sale.SaleNumber, cashierName,
            sale.CompletedAtUtc ?? sale.CreatedAtUtc, lines,
            sale.Subtotal, sale.DiscountTotal, sale.TaxTotal, sale.DeliveryCharge, sale.GrandTotal,
            payments, sale.ChangeDue, sale.Status,
            HeaderText: settings.SalesHeaderText,
            FooterText: settings.SalesFooterText,
            BusinessAddress: businessAddress,
            BusinessContactNumber: branch?.ContactNumber ?? profile.ContactNumber,
            TaxId: profile.TaxId,
            ShowBranch: settings.SalesShowBranch,
            ShowCashier: settings.SalesShowCashier,
            ShowPaymentMethod: settings.SalesShowPaymentMethod,
            ShowTaxLine: settings.SalesShowTaxLine,
            ShowReferenceNumber: settings.SalesShowReferenceNumber,
            Width: settings.Width);
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "SaleQueryTests|ReceiptTests"`
Expected: PASS — new tests plus every pre-existing test in both files (proves nothing else in either DTO shifted).

- [ ] **Step 5: Commit**

```bash
git add src/Negosio.Application/Sales/SaleContracts.cs src/Negosio.Application/Sales/SaleQueryService.cs src/Negosio.Application/Sales/ReceiptService.cs tests/Negosio.IntegrationTests/Sales/SaleQueryTests.cs tests/Negosio.IntegrationTests/Sales/ReceiptTests.cs
git commit -m "feat(sales): surface the delivery charge on sale detail and the printed receipt"
```

---

### Task 6: Full backend regression pass

**Files:** none (verification checkpoint only).

- [ ] **Step 1:** Run the full backend suite: `dotnet test`
Expected: PASS, 0 failures — this is the point where any of the ~15 pre-existing call sites of `CheckoutRequest`/`SaleDetailDto`/`ReceiptDto`/`DeliveryReceiptDto` across `Branches/`, `Registers/`, `Reports/`, `Staff/`, `Sales/` (found via the earlier `grep` of `new CheckoutRequest(`) would surface a break, if one exists — they were all confirmed to use short positional calls with no `Approval:`/other trailing named args, so none should need edits, but this run is the proof.
- [ ] **Step 2:** If anything fails, fix forward in the file it broke in (most likely a missed positional-record call site) and re-run before moving to the frontend.

No commit for this task — it's a checkpoint; the fix (if any) belongs in whichever backend commit above it logically extends, or its own tiny `fix:` commit if the suite is otherwise green from Task 6.

---

## Frontend

### Task 7: Types and shared helpers carry the delivery charge

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/lib/format.ts`
- Modify: `web/negosio-web/src/lib/pos.ts`

**Interfaces:**
- Produces: `CheckoutRequest.deliveryCharge`, `SaleResultDto.deliveryCharge`, `SaleDetailDto.deliveryCharge`, `ReceiptDto.deliveryCharge`, `DeliveryReceiptDto.deliveryCharge` (all `number`); `formatDeliveryCharge(n: number): string`; `EMPTY_DELIVERY_CHARGE` (`'0.00'`), `parseDeliveryCharge(forDelivery: boolean, raw: string): number`, `isValidDeliveryChargeInput(raw: string): boolean` — every component task below imports these.

No automated test for this task (no frontend test runner exists in this repo) — verified by `tsc` at the end of Task 10 once call sites exist, and directly here by the compiler already complaining about any TS syntax mistakes.

- [ ] **Step 1: Update `web/negosio-web/src/api/types.ts`**

Replace the `CheckoutRequest` interface (lines 479-488):

```ts
export interface CheckoutRequest {
  branchId: string
  registerSessionId: string
  clientRequestId: string
  items: CheckoutItemInput[]
  payments: CheckoutPaymentInput[]
  /** The delivery fee for a "for delivery" sale, 0 for a normal sale. */
  deliveryCharge: number
  /** Manager/Admin/Owner approval — only sent when retrying after a DISCOUNT_APPROVAL_REQUIRED
   * rejection (the sale carries a line discount the cashier can't apply directly). */
  approval?: VoidSaleApprovalInput
}
```

Replace `SaleResultDto` (lines 490-501):

```ts
export interface SaleResultDto {
  saleId: string
  saleNumber: string
  status: SaleStatus
  subtotal: number
  discountTotal: number
  taxTotal: number
  deliveryCharge: number
  grandTotal: number
  amountPaid: number
  changeDue: number
  wasExistingRequest: boolean
}
```

Replace `SaleDetailDto` (lines 613-633) — add `deliveryCharge` after `taxTotal`:

```ts
export interface SaleDetailDto {
  sale: SaleSummaryDto
  registerSessionId: string
  subtotal: number
  discountTotal: number
  taxTotal: number
  deliveryCharge: number
  amountPaid: number
  changeDue: number
  completedAtUtc: string | null
  items: SaleItemDto[]
  payments: SalePaymentDto[]
  returns: SaleReturnDto[]
  voidedByUserId: string | null
  voidedByName: string | null
  approvedByUserId: string | null
  approvedByName: string | null
  voidReason: string | null
  voidedAtUtc: string | null
  canVoid: boolean
  voidIneligibilityCode: string | null
}
```

Replace `ReceiptDto` (lines 657-683) — add `deliveryCharge` after `taxTotal`:

```ts
export interface ReceiptDto {
  storeName: string
  branchName: string
  registerName: string
  saleNumber: string
  cashierName: string
  createdAtUtc: string
  lines: ReceiptLineDto[]
  subtotal: number
  discountTotal: number
  taxTotal: number
  deliveryCharge: number
  grandTotal: number
  payments: ReceiptPaymentDto[]
  changeDue: number
  status: SaleStatus
  headerText: string | null
  footerText: string | null
  businessAddress: string | null
  businessContactNumber: string | null
  taxId: string | null
  showBranch: boolean
  showCashier: boolean
  showPaymentMethod: boolean
  showTaxLine: boolean
  showReferenceNumber: boolean
  width: ReceiptWidth
}
```

Replace `DeliveryReceiptDto` (lines 700-721) — add `deliveryCharge` after `items`:

```ts
export interface DeliveryReceiptDto {
  id: string
  createdAtUtc: string
  relatedSaleNumber: string | null
  branchName: string
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  deliveryNotes: string | null
  preparedByName: string
  items: DeliveryReceiptItemDto[]
  deliveryCharge: number
  headerText: string | null
  footerText: string | null
  businessName: string
  businessAddress: string | null
  businessContactNumber: string | null
  taxId: string | null
  showPrices: boolean
  showRelatedSaleNumber: boolean
  showContactNumber: boolean
  showSignatureFields: boolean
}
```

- [ ] **Step 2: Add `formatDeliveryCharge` to `web/negosio-web/src/lib/format.ts`**

Read the file first to match its exact existing `formatMoney` export shape, then add immediately after it:

```ts
/** Same as formatMoney, but renders exactly 0 as "Free" — used only where a $0 value is known to
 * mean free delivery (a DeliveryReceipt only ever exists for a delivery sale), never on a plain
 * sales total where 0 could just as easily mean "not a delivery sale at all". */
export function formatDeliveryCharge(n: number): string {
  return n <= 0 ? 'Free' : formatMoney(n)
}
```

- [ ] **Step 3: Add delivery-charge helpers to `web/negosio-web/src/lib/pos.ts`**

Add after `EMPTY_DELIVERY_FIELDS` (line 88) — deliberately a sibling of, not a member of, `DeliveryFields`/`EMPTY_DELIVERY_FIELDS`: `DeliveryFields` maps 1:1 to `CreateDeliveryReceiptRequest`'s payload, but the charge is a `CheckoutRequest` field, not a delivery-receipt field, so it gets its own state and constant:

```ts
/** Default/reset value for the delivery-charge input — a formatted string so the field always
 * starts showing "0.00", matching how a cashier would type a peso amount. */
export const EMPTY_DELIVERY_CHARGE = '0.00'

const DELIVERY_CHARGE_PATTERN = /^\d+(\.\d{1,2})?$/

/** True only for a non-negative amount with at most 2 decimal places — the same rule the backend
 * enforces in CheckoutRequestValidator. Checked against the raw string (not a parsed float) so a
 * value like "10.005" is rejected exactly, with no floating-point rounding ambiguity. */
export function isValidDeliveryChargeInput(raw: string): boolean {
  return DELIVERY_CHARGE_PATTERN.test(raw.trim())
}

/** The delivery charge to actually use for live total/change math: 0 whenever delivery isn't
 * selected or the typed value isn't a valid non-negative number yet (mid-typing), so the running
 * total never shows NaN or a negative figure — Confirm payment is separately blocked until the
 * value passes isValidDeliveryChargeInput. */
export function parseDeliveryCharge(forDelivery: boolean, raw: string): number {
  if (!forDelivery) return 0
  const n = Number(raw)
  return Number.isFinite(n) && n > 0 ? n : 0
}
```

- [ ] **Step 4: Commit**

```bash
git add web/negosio-web/src/api/types.ts web/negosio-web/src/lib/format.ts web/negosio-web/src/lib/pos.ts
git commit -m "feat(pos): frontend types and helpers for the delivery charge"
```

---

### Task 8: Delivery-charge input inside the existing delivery section

**Files:**
- Modify: `web/negosio-web/src/components/pos/DeliveryDetailsFields.tsx`

**Interfaces:**
- Consumes: `isValidDeliveryChargeInput` from Task 7 (used by the parent, not this file, to compute the `error` prop — this component stays purely presentational, matching its existing doc comment).
- Produces: two new props (`deliveryCharge: string`, `onDeliveryChargeChange: (v: string) => void`) plus `deliveryChargeError?: string` added to the existing `errors` prop shape — Task 9 wires these from `PaymentModal`.

- [ ] **Step 1: Implement**

Replace the full contents of `web/negosio-web/src/components/pos/DeliveryDetailsFields.tsx`:

```tsx
import type { DeliveryFields } from '../../lib/pos'
import { TextArea, TextField } from '../ui'

interface Props {
  values: DeliveryFields
  onChange: (patch: Partial<DeliveryFields>) => void
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
  errors: { recipientName?: string; deliveryAddress?: string; deliveryCharge?: string }
  disabled?: boolean
}

/** Presentational: the delivery inputs, rendered directly inside the payment modal's
 * "For delivery" section (no card of their own). All state and validation live in the parent
 * (PaymentModal) so the values survive a failed-payment retry. */
export function DeliveryDetailsFields({
  values,
  onChange,
  deliveryCharge,
  onDeliveryChargeChange,
  errors,
  disabled,
}: Props) {
  const isFree = errors.deliveryCharge == null && Number(deliveryCharge) === 0

  return (
    <div className="mt-3 space-y-3 border-t border-border pt-3">
      <TextField
        label="Recipient name *"
        name="recipientName"
        placeholder="Enter recipient name"
        value={values.recipientName}
        onChange={(e) => onChange({ recipientName: e.target.value })}
        error={errors.recipientName || undefined}
        disabled={disabled}
      />
      <TextArea
        label="Recipient address *"
        name="deliveryAddress"
        rows={2}
        placeholder="Enter delivery address"
        value={values.deliveryAddress}
        onChange={(e) => onChange({ deliveryAddress: e.target.value })}
        error={errors.deliveryAddress || undefined}
        disabled={disabled}
      />
      <div>
        <TextField
          label="Delivery charge (₱)"
          name="deliveryCharge"
          type="number"
          min={0}
          step="0.01"
          value={deliveryCharge}
          onChange={(e) => onDeliveryChargeChange(e.target.value)}
          error={errors.deliveryCharge || undefined}
          disabled={disabled}
        />
        {isFree && <p className="mt-1 text-[12px] text-text-muted">Free delivery</p>}
      </div>
      <div className="grid gap-x-3 sm:grid-cols-2">
        <TextField
          label="Contact number"
          name="contactNumber"
          placeholder="Enter contact number"
          value={values.contactNumber}
          onChange={(e) => onChange({ contactNumber: e.target.value })}
          disabled={disabled}
        />
        <TextField
          label="Delivery notes (optional)"
          name="deliveryNotes"
          placeholder="e.g. Leave at the front desk"
          value={values.deliveryNotes}
          onChange={(e) => onChange({ deliveryNotes: e.target.value })}
          disabled={disabled}
        />
      </div>
    </div>
  )
}
```

- [ ] **Step 2: Commit**

```bash
git add web/negosio-web/src/components/pos/DeliveryDetailsFields.tsx
git commit -m "feat(pos): add the delivery-charge input to the delivery section"
```

(Compiles but is not wired up yet — `PaymentModal` doesn't pass the new props until Task 9. `npx tsc --noEmit` will fail between this commit and the next; that's expected mid-plan and is resolved by Task 9.)

---

### Task 9: `PaymentModal` computes the effective total with the delivery charge and fixes the non-cash payment amount

**Files:**
- Modify: `web/negosio-web/src/components/pos/PaymentModal.tsx`

**Interfaces:**
- Consumes: `deliveryCharge`/`onDeliveryChargeChange` props (Task 10 adds these as controlled state from `PosTerminal`, same pattern as the existing `deliveryFields`/`onDeliveryFieldsChange`); `isValidDeliveryChargeInput`/`parseDeliveryCharge` from Task 7.
- Produces: nothing new for other tasks to consume — this is a leaf UI change — but fixes a real bug: the non-cash `onConfirm({ method, amount: amountDue, ... })` call currently sends the pre-delivery `amountDue` prop verbatim, which would be short by the delivery charge and trip `PAYMENT_INSUFFICIENT` on the backend once Task 2 is live. This task must land before delivery-charge checkout is exercised through a non-cash method.

- [ ] **Step 1: Implement**

In `web/negosio-web/src/components/pos/PaymentModal.tsx`:

Update the import line to pull in the new helpers:

```tsx
import {
  POS_PAYMENT_METHODS,
  PAYMENT_METHOD_LABELS,
  REFERENCE_LABELS,
  isValidDeliveryChargeInput,
  parseDeliveryCharge,
  suggestCashButtons,
  type DeliveryFields,
} from '../../lib/pos'
```

Add two new props to the `Props` interface, right after `onDeliveryFieldsChange`:

```tsx
  /** Patch one or more delivery fields. */
  onDeliveryFieldsChange: (patch: Partial<DeliveryFields>) => void
  /** The typed delivery-charge string, owned by the parent for the same reason deliveryFields is —
   * it must survive the modal closing and reopening after a failed-payment retry. */
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
```

Add the two new props to the destructured function signature (right after `onDeliveryFieldsChange,`):

```tsx
  onDeliveryFieldsChange,
  deliveryCharge,
  onDeliveryChargeChange,
}: Props) {
```

Replace the `change`/`deliveryComplete`/`deliveryErrors`/`paymentComplete`/`canConfirm`/`confirm` block (lines 60-94) with:

```tsx
  const receivedNum = Number(received)
  const deliveryChargeValid = !forDelivery || isValidDeliveryChargeInput(deliveryCharge)
  const effectiveAmountDue = amountDue + parseDeliveryCharge(forDelivery, deliveryCharge)
  const change = method === 'Cash' ? Math.max(0, receivedNum - effectiveAmountDue) : 0

  const deliveryComplete =
    !forDelivery ||
    (deliveryFields.recipientName.trim() !== '' &&
      deliveryFields.deliveryAddress.trim() !== '' &&
      deliveryChargeValid)
  const deliveryErrors =
    forDelivery && deliveryAttempted
      ? {
          recipientName: deliveryFields.recipientName.trim() ? undefined : 'Recipient name is required.',
          deliveryAddress: deliveryFields.deliveryAddress.trim()
            ? undefined
            : 'Recipient address is required.',
          deliveryCharge: deliveryChargeValid
            ? undefined
            : 'Enter a valid amount (0 or more, up to 2 decimal places).',
        }
      : {}

  const paymentComplete =
    method !== 'Cash' || (received.trim() !== '' && receivedNum >= effectiveAmountDue)
  // The button stays enabled while delivery fields are incomplete — clicking it then surfaces the
  // inline errors rather than silently doing nothing. It only hard-disables for an incomplete
  // payment or an in-flight submit.
  const canConfirm = !submitting && paymentComplete

  const confirm = () => {
    if (submitting || !paymentComplete) return
    if (forDelivery && !deliveryComplete) {
      setDeliveryAttempted(true)
      return
    }
    if (method === 'Cash') {
      onConfirm({ method: 'Cash', receivedAmount: receivedNum })
    } else {
      onConfirm({ method, amount: effectiveAmountDue, referenceNumber: reference.trim() || null })
    }
  }
```

Update every other use of `amountDue` inside the JSX to `effectiveAmountDue` — specifically the "Amount due" banner (line 117) and `suggestCashButtons(amountDue)` (line 164):

```tsx
        <div className="mb-4 rounded-lg bg-surface-subtle px-3 py-3 text-center">
          <p className="text-[12px] uppercase tracking-wide text-text-muted">Amount due</p>
          <p className="text-2xl font-bold text-text-primary">{formatMoney(effectiveAmountDue)}</p>
        </div>
```

```tsx
            <div className="flex flex-wrap gap-1.5">
              {suggestCashButtons(effectiveAmountDue).map((amt) => (
```

Finally, pass the two new props down to `DeliveryDetailsFields` (lines 206-213):

```tsx
          {forDelivery && (
            <DeliveryDetailsFields
              values={deliveryFields}
              onChange={onDeliveryFieldsChange}
              deliveryCharge={deliveryCharge}
              onDeliveryChargeChange={onDeliveryChargeChange}
              errors={deliveryErrors}
              disabled={submitting}
            />
          )}
```

- [ ] **Step 2: Commit**

```bash
git add web/negosio-web/src/components/pos/PaymentModal.tsx
git commit -m "feat(pos): PaymentModal folds the delivery charge into the effective total"
```

(`npx tsc --noEmit` still fails after this commit — `PosTerminal` doesn't pass the new required props yet. Resolved by Task 10, next.)

---

### Task 10: `PosTerminal` owns the delivery-charge state, resets, and checkout payload

**Files:**
- Modify: `web/negosio-web/src/components/pos/PosTerminal.tsx`

**Interfaces:**
- Consumes: `EMPTY_DELIVERY_CHARGE`, `parseDeliveryCharge` from Task 7; the new `PaymentModal` props from Task 9.
- Produces: the `deliveryCharge: forDelivery ? roundMoney(...) : 0` field on the outgoing `CheckoutRequest` body — this is the payload Task 2's backend validator/service actually receives.

- [ ] **Step 1: Implement**

In `web/negosio-web/src/components/pos/PosTerminal.tsx`:

Update the `lib/pos` and `lib/saleMath` imports:

```tsx
import { calcTotals, roundMoney } from '../../lib/saleMath'
import type { DeliveryFields, LastSaleRef } from '../../lib/pos'
import { EMPTY_DELIVERY_CHARGE, EMPTY_DELIVERY_FIELDS, parseDeliveryCharge, VOID_INELIGIBLE_MESSAGES } from '../../lib/pos'
```

Add the new state right after the existing `deliveryFields` state (line 106):

```tsx
  const [forDelivery, setForDelivery] = useState(false)
  const [deliveryFields, setDeliveryFields] = useState<DeliveryFields>(EMPTY_DELIVERY_FIELDS)
  const [deliveryCharge, setDeliveryCharge] = useState(EMPTY_DELIVERY_CHARGE)
```

Add the reset in `confirmNewTransaction` (after line 242's `setDeliveryFields(EMPTY_DELIVERY_FIELDS)`):

```tsx
    setForDelivery(false)
    setDeliveryFields(EMPTY_DELIVERY_FIELDS)
    setDeliveryCharge(EMPTY_DELIVERY_CHARGE)
    setSuccessDeliveryReceiptId(null)
```

Update `onToggleForDelivery` (lines 248-251) to also reset the charge on uncheck:

```tsx
  // Ticking "For delivery" expands the inline section; unticking clears whatever was typed so a
  // hidden payload can never be submitted, and re-ticking starts from a clean form.
  const onToggleForDelivery = useCallback((next: boolean) => {
    setForDelivery(next)
    if (!next) {
      setDeliveryFields(EMPTY_DELIVERY_FIELDS)
      setDeliveryCharge(EMPTY_DELIVERY_CHARGE)
    }
  }, [])
```

Add the change handler right after `onDeliveryFieldsChange` (line 255):

```tsx
  const onDeliveryFieldsChange = useCallback((patch: Partial<DeliveryFields>) => {
    setDeliveryFields((f) => ({ ...f, ...patch }))
  }, [])

  const onDeliveryChargeChange = useCallback((value: string) => {
    setDeliveryCharge(value)
  }, [])
```

Add the delivery charge to the checkout body inside `mutation`'s `mutationFn` (lines 397-408):

```tsx
      const body: CheckoutRequest = {
        branchId,
        registerSessionId: ctx.registerSessionId,
        clientRequestId,
        items: cart.lines.map((l) => ({
          productVariantId: l.variantId,
          quantity: l.quantity,
          discount: l.discount.type === 'None' ? null : l.discount,
        })),
        payments: [payment],
        deliveryCharge: roundMoney(parseDeliveryCharge(forDelivery, deliveryCharge)),
        approval,
      }
```

Add the charge reset alongside the existing post-success reset (lines 443-444):

```tsx
      setForDelivery(false)
      setDeliveryFields(EMPTY_DELIVERY_FIELDS)
      setDeliveryCharge(EMPTY_DELIVERY_CHARGE)
```

Wire the two new `PaymentModal` props (around line 615-618):

```tsx
        forDelivery={forDelivery}
        onToggleForDelivery={onToggleForDelivery}
        deliveryFields={deliveryFields}
        onDeliveryFieldsChange={onDeliveryFieldsChange}
        deliveryCharge={deliveryCharge}
        onDeliveryChargeChange={onDeliveryChargeChange}
```

Update the `PaymentFailedModal`'s `amount` prop (line 644) so a failed-payment retry screen shows the correct total including the charge:

```tsx
        amount={totals.grandTotal + roundMoney(parseDeliveryCharge(forDelivery, deliveryCharge))}
```

- [ ] **Step 2: Run TypeScript + build**

Run: `cd web/negosio-web && npx tsc --noEmit && npm run build`
Expected: PASS — this is the point where every prop/type added across Tasks 8-11 must line up; fix any mismatch before proceeding.

- [ ] **Step 3: Commit**

```bash
git add web/negosio-web/src/components/pos/PosTerminal.tsx
git commit -m "feat(pos): PosTerminal owns delivery-charge state and sends it at checkout"
```

---

### Task 11: Payment-success summary shows the delivery charge

**Files:**
- Modify: `web/negosio-web/src/components/pos/PaymentSuccessModal.tsx`

**Interfaces:**
- Consumes: `result.deliveryCharge` (from `SaleResultDto`, Task 2/7) and `formatDeliveryCharge` (Task 7).

Gating note: this row is shown only when `deliveryReceiptId` is set, exactly like the existing "Delivery receipt ready" banner right below it — `deliveryReceiptId` is the one piece of state in this flow that reliably means "this was a delivery sale" (a `result.deliveryCharge` of `0` is ambiguous between "not a delivery sale" and "free delivery"; whether a `DeliveryReceipt` exists is not). This intentionally means the row appears a beat after the modal opens, once the delivery-receipt POST resolves — identical timing to the existing banner, already shipped and tested.

- [ ] **Step 1: Implement**

In `web/negosio-web/src/components/pos/PaymentSuccessModal.tsx`, update the import:

```tsx
import { formatDeliveryCharge, formatMoney } from '../../lib/format'
```

Add the row inside the `<dl>` (after the "Amount paid" row, line 85):

```tsx
        <SummaryRow label="Amount paid" value={formatMoney(result.amountPaid)} strong />
        {deliveryReceiptId && (
          <SummaryRow label="Delivery charge" value={formatDeliveryCharge(result.deliveryCharge)} />
        )}
        {payment && <SummaryRow label="Payment method" value={PAYMENT_METHOD_LABELS[payment.method]} />}
```

- [ ] **Step 2: Commit**

```bash
git add web/negosio-web/src/components/pos/PaymentSuccessModal.tsx
git commit -m "feat(pos): show the delivery charge on the payment-success summary"
```

---

### Task 12: Printed delivery receipt shows the delivery fee

**Files:**
- Modify: `web/negosio-web/src/pages/DeliveryReceiptPage.tsx`

**Interfaces:**
- Consumes: `d.deliveryCharge` (`DeliveryReceiptDto`, Task 4/7) and `formatDeliveryCharge` (Task 7). A `DeliveryReceipt` only ever exists for a delivery sale, so there is no ambiguity here — the row is always shown (gated only by the existing `showPriceColumns`/`DeliveryShowPrices` setting, consistent with every other monetary value on this document).

- [ ] **Step 1: Implement**

In `web/negosio-web/src/pages/DeliveryReceiptPage.tsx`, update the import:

```tsx
import { formatDeliveryCharge, formatMoney, formatQty } from '../lib/format'
```

Add a row after the items `<table>` and before the `deliveryNotes` block (after line 121):

```tsx
        {showPriceColumns && (
          <div className="dr-line">
            <span className="label">Delivery fee:</span> {formatDeliveryCharge(d.deliveryCharge)}
          </div>
        )}

        {d.deliveryNotes && d.deliveryNotes.trim() && (
```

- [ ] **Step 2: Commit**

```bash
git add web/negosio-web/src/pages/DeliveryReceiptPage.tsx
git commit -m "feat(delivery): show the delivery fee on the printed delivery receipt"
```

---

### Task 13: Sale detail page and printed sales receipt show the delivery charge

**Files:**
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`
- Modify: `web/negosio-web/src/pages/ReceiptPage.tsx`

**Interfaces:**
- Consumes: `d.deliveryCharge` (`SaleDetailDto`) / `r.deliveryCharge` (`ReceiptDto`) from Tasks 5/7.

Gating note: both rows are shown only when `deliveryCharge > 0`, matching the existing "Discount" row precedent in `ReceiptPage.tsx` (`{r.discountTotal > 0 && ...}`). This is a deliberate, documented trade-off: a `deliveryCharge` of exactly `0` is ambiguous here between "not a delivery sale" (the overwhelming majority of sales) and "a free-delivery sale" — unlike `PaymentSuccessModal`/`DeliveryReceiptPage` (Tasks 11-12), neither of these two pages currently has an unambiguous "this was a delivery sale" signal at hand without adding a new cross-cutting flag, and the spec's own free-delivery acceptance test only requires the Delivery Receipt to exist and the total to stay correct — not that a `0` line appears on the plain sales documents. If the user later wants a "Free delivery" line to appear here too, `SaleDetailPage` already fetches `deliveryReceiptQuery` and could gate on that instead — flag it as a known follow-up, don't build it speculatively now.

- [ ] **Step 1: Implement `SaleDetailPage.tsx`**

Add a row between "Tax" and "Total" in the summary `<dl>` (after line 203):

```tsx
                    <div className="flex justify-between text-text-secondary">
                      <dt>Tax</dt>
                      <dd>{formatMoney(d.taxTotal)}</dd>
                    </div>
                    {d.deliveryCharge > 0 && (
                      <div className="flex justify-between text-text-secondary">
                        <dt>Delivery charge</dt>
                        <dd>{formatMoney(d.deliveryCharge)}</dd>
                      </div>
                    )}
                    <div className="flex justify-between border-t border-border pt-1.5 text-base font-bold text-text-primary">
```

- [ ] **Step 2: Implement `ReceiptPage.tsx`**

Add a row between the tax line and `TOTAL`, following the existing `discountTotal > 0` pattern (after line 122):

```tsx
        {r.showTaxLine && (
          <div className="row">
            <span>Tax</span>
            <span className="r">{formatMoney(r.taxTotal)}</span>
          </div>
        )}
        {r.deliveryCharge > 0 && (
          <div className="row">
            <span>Delivery charge</span>
            <span className="r">{formatMoney(r.deliveryCharge)}</span>
          </div>
        )}
        <div className="row bold">
```

- [ ] **Step 3: Run TypeScript + build**

Run: `cd web/negosio-web && npx tsc --noEmit && npm run build`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add web/negosio-web/src/pages/SaleDetailPage.tsx web/negosio-web/src/pages/ReceiptPage.tsx
git commit -m "feat(sales): show the delivery charge on sale detail and the printed sales receipt"
```

---

### Task 14: Backend — Delivery Report (contracts, service, endpoint, tests)

Adds `GET /api/reports/deliveries` to the **existing** Reports module — one more method on `IReportsService`/`ReportsService` and one more action on `ReportsController`, not a new service or controller. Sources rows from `DeliveryReceipt` joined to `Sale` on `SaleId`; the charge and grand total are read live off `Sale` (never duplicated), matching Task 2's design. This is the first *paged* report in the module — every existing report is a small aggregate — so it copies `SaleQueryService.ListAsync`'s pagination/search idiom (`PagedResult<T>`, `.Contains(term)`, hardcoded `OrderByDescending` newest-first) rather than `ReportsContracts.cs`'s aggregate-only shapes, and copies `ReportsService.ComputeKpisAsync`'s "several small separately-awaited SUM/COUNT queries against the same filtered base" idiom for the summary totals.

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/DeliveryReportTests.cs` (new)

**Interfaces:**
- Consumes: `Sale.DeliveryCharge` (Task 1); `DeliveryReceipt`'s existing fields (`SaleId`, `RelatedSaleNumber`, `RecipientName`, `DeliveryAddress`, `ContactNumber`, `DeliveryNotes`, `PreparedByNameSnapshot`, `CreatedAtUtc`, `BranchId`) — no entity changes needed; `IBranchAccessResolver.ResolveListFilterAsync` and `PagedResult<T>` (both pre-existing, unchanged).
- Produces: `GET /api/reports/deliveries` → `DeliveryReportResultDto` — Task 15's frontend page consumes this exactly (property-for-property, camelCase over the wire as everywhere else in this API).

- [ ] **Step 1: Write the failing tests**

Create `tests/Negosio.IntegrationTests/Reports/DeliveryReportTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Reports;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Reports;

public class DeliveryReportTests : IntegrationTest
{
    public DeliveryReportTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private static CreateDeliveryReceiptRequest Req(string recipientName = "Juan Dela Cruz") =>
        new(recipientName, "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse", null);

    private async Task<(Guid BranchId, Guid SessionId, Guid VariantId)> ArrangeAsync(Guid branchId, decimal price = 100m)
    {
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: 20m);
        return (branchId, session.Id, variantId);
    }

    private async Task<Guid> DeliverySaleAsync(
        Guid branchId, Guid sessionId, Guid variantId, decimal price, decimal deliveryCharge, string recipientName = "Juan Dela Cruz")
    {
        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, sessionId, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: price + deliveryCharge + 100m) },
            DeliveryCharge: deliveryCharge));
        (await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipt", Req(recipientName)))
            .EnsureSuccessStatusCode();
        return sale.SaleId;
    }

    [Fact]
    public async Task Only_sales_with_a_delivery_receipt_appear_and_totals_cover_the_whole_filtered_set()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var (b, session, variant) = await ArrangeAsync(branchId, price: 100m);

        await DeliverySaleAsync(b, session, variant, 100m, 60m); // charged delivery
        await DeliverySaleAsync(b, session, variant, 100m, 0m);  // free delivery
        // A normal sale with no delivery receipt at all — must never appear in the report.
        await CheckoutOkAsync(new CheckoutRequest(
            b, session, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variant, 1m, null) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 200m) }));

        var result = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        result!.Page.TotalCount.Should().Be(2);
        result.Totals.TotalDeliveries.Should().Be(2);
        result.Totals.FreeDeliveries.Should().Be(1);
        result.Totals.ChargedDeliveries.Should().Be(1);
        result.Totals.TotalDeliveryCharges.Should().Be(60m);
        result.Totals.AverageDeliveryCharge.Should().Be(30m);
        result.Page.Items.Should().OnlyContain(r => r.DeliveryCharge == 60m || r.DeliveryCharge == 0m);
    }

    [Fact]
    public async Task Search_matches_recipient_name_and_related_sale_number()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var (b, session, variant) = await ArrangeAsync(branchId, price: 50m);

        var saleId = await DeliverySaleAsync(b, session, variant, 50m, 20m, recipientName: "Maria Santos");
        await DeliverySaleAsync(b, session, variant, 50m, 0m, recipientName: "Pedro Reyes");

        var byRecipient = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            "/api/reports/deliveries?search=Maria", TestJson.Options);
        byRecipient!.Page.Items.Should().ContainSingle(r => r.RecipientName == "Maria Santos");

        var saleNumber = (await Client.GetFromJsonAsync<SaleDetailDto>($"/api/sales/{saleId}", TestJson.Options))!.Sale.SaleNumber;
        var byNumber = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            $"/api/reports/deliveries?search={saleNumber}", TestJson.Options);
        byNumber!.Page.Items.Should().ContainSingle(r => r.SaleNumber == saleNumber);
    }

    [Fact]
    public async Task Newest_delivery_is_first_by_default()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var (b, session, variant) = await ArrangeAsync(branchId, price: 40m);

        await DeliverySaleAsync(b, session, variant, 40m, 10m, recipientName: "First");
        await DeliverySaleAsync(b, session, variant, 40m, 10m, recipientName: "Second");

        var result = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        result!.Page.Items[0].RecipientName.Should().Be("Second");
        result.Page.Items[1].RecipientName.Should().Be("First");
    }

    [Fact]
    public async Task Manager_is_forced_to_their_own_branch_owner_sees_tenant_wide()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainBranchId = await GetMainBranchIdAsync(owner);
        var other = await CreateBranchAsync("BGC", "BGC");

        var (mainBranch, mainSession, mainVariant) = await ArrangeAsync(mainBranchId, price: 30m);
        await DeliverySaleAsync(mainBranch, mainSession, mainVariant, 30m, 15m);

        var (otherBranch, otherSession, otherVariant) = await ArrangeAsync(other.Id, price: 30m);
        await DeliverySaleAsync(otherBranch, otherSession, otherVariant, 30m, 25m);

        var ownerResult = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        ownerResult!.Page.TotalCount.Should().Be(2);

        var managerToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainBranchId);
        Authorize(managerToken);
        var managerResult = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);
        managerResult!.Page.TotalCount.Should().Be(1);
        managerResult.Page.Items[0].DeliveryCharge.Should().Be(15m);
    }

    [Fact]
    public async Task Cashier_cannot_reach_the_delivery_report()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var cashierToken = await AddTenantUserTokenAsync("cara@example.com", UserRole.Cashier, branchId);
        Authorize(cashierToken);

        var response = await Client.GetAsync("/api/reports/deliveries");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReportTests`
Expected: FAIL to compile — `DeliveryReportResultDto` doesn't exist and `GET /api/reports/deliveries` doesn't exist (404).

- [ ] **Step 3: Implement the contracts**

In `src/Negosio.Application/Reports/ReportsContracts.cs`, add `using Negosio.Application.Common;` to the top-of-file usings (needed for `PagedResult<T>`), and add these types (anywhere after `ReportFilter`, e.g. right before `public interface IReportsService`):

```csharp
public sealed record DeliveryReportQuery(
    Guid? BranchId = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    string? Search = null,
    int Page = 1,
    int PageSize = PagedResult<DeliveryReportRowDto>.DefaultPageSize);

/// <summary>One delivered sale. <see cref="DeliveryCharge"/> and <see cref="SaleGrandTotal"/> are
/// read live from the linked Sale — DeliveryReceipt never stores its own copy of either, so this
/// row can never drift from the Sale that is the actual source of truth (see the plan's Global
/// Constraints).</summary>
public sealed record DeliveryReportRowDto(
    Guid DeliveryReceiptId,
    Guid SaleId,
    string SaleNumber,
    DateTime CreatedAtUtc,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    decimal DeliveryCharge,
    decimal SaleGrandTotal,
    string PaymentSummary,
    string PreparedByName);

/// <summary>Aggregates over the FULL filtered set, not just the current page — computed the same
/// way <see cref="ReportsService"/>'s KPI queries are: separate SUM/COUNT queries against the same
/// filtered base, before paging is applied.</summary>
public sealed record DeliveryReportTotalsDto(
    int TotalDeliveries,
    int FreeDeliveries,
    int ChargedDeliveries,
    decimal TotalDeliveryCharges,
    decimal AverageDeliveryCharge);

public sealed record DeliveryReportResultDto(PagedResult<DeliveryReportRowDto> Page, DeliveryReportTotalsDto Totals);
```

Add one method to `IReportsService`:

```csharp
public interface IReportsService
{
    Task<ReportsOverviewDto> GetOverviewAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TopProductDto>> GetTopProductsAsync(ReportFilter filter, int top, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CategoryPerformanceDto>> GetCategoryPerformanceAsync(ReportFilter filter, CancellationToken cancellationToken = default);

    Task<DeliveryReportResultDto> GetDeliveriesAsync(DeliveryReportQuery query, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Implement the service method**

In `src/Negosio.Application/Reports/ReportsService.cs`, add this method to the `ReportsService` class (e.g. right after `GetCategoryPerformanceAsync`, before the `// ---- shared filtering ----` region):

```csharp
    public async Task<DeliveryReportResultDto> GetDeliveriesAsync(DeliveryReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);

        var receipts = _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.SaleId != null);

        if (branchFilter is { } b)
        {
            receipts = receipts.Where(d => d.BranchId == b);
        }

        if (query.FromUtc is { } fromUtc)
        {
            receipts = receipts.Where(d => d.CreatedAtUtc >= fromUtc);
        }

        if (query.ToUtc is { } toUtc)
        {
            receipts = receipts.Where(d => d.CreatedAtUtc <= toUtc);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            receipts = receipts.Where(d =>
                (d.RelatedSaleNumber != null && d.RelatedSaleNumber.Contains(term)) ||
                d.RecipientName.Contains(term) ||
                d.DeliveryAddress.Contains(term) ||
                (d.ContactNumber != null && d.ContactNumber.Contains(term)));
        }

        // The delivery charge and sale total are read live from Sale — DeliveryReceipt never stores
        // its own copy, so they're joined here rather than duplicated at write time.
        var joined =
            from dr in receipts
            join s in _db.Sales.AsNoTracking().Where(x => x.TenantId == tenantId) on dr.SaleId equals s.Id
            select new { dr, s };

        var chargesQuery = joined.Select(x => x.s.DeliveryCharge);
        var totalDeliveries = await chargesQuery.CountAsync(cancellationToken);
        var freeDeliveries = await chargesQuery.CountAsync(c => c == 0m, cancellationToken);
        var totalCharges = totalDeliveries == 0 ? 0m : await chargesQuery.SumAsync(cancellationToken);
        var averageCharge = totalDeliveries == 0 ? 0m : Money.Round(totalCharges / totalDeliveries);
        var totals = new DeliveryReportTotalsDto(totalDeliveries, freeDeliveries, totalDeliveries - freeDeliveries, totalCharges, averageCharge);

        var projected = joined
            .OrderByDescending(x => x.dr.CreatedAtUtc)
            .Select(x => new DeliveryReportRow(
                x.dr.Id, x.s.Id, x.s.SaleNumber, x.dr.CreatedAtUtc,
                x.dr.RecipientName, x.dr.DeliveryAddress, x.dr.ContactNumber, x.dr.DeliveryNotes,
                x.s.DeliveryCharge, x.s.GrandTotal, x.dr.PreparedByNameSnapshot,
                _db.Payments.Where(p => p.SaleId == x.s.Id).Select(p => p.Method).Distinct().ToList()));

        var rows = await PagedResult<DeliveryReportRow>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = rows.Items.Select(r => new DeliveryReportRowDto(
            r.DeliveryReceiptId, r.SaleId, r.SaleNumber, r.CreatedAtUtc,
            r.RecipientName, r.DeliveryAddress, r.ContactNumber, r.DeliveryNotes,
            r.DeliveryCharge, r.SaleGrandTotal, string.Join(" + ", r.Methods.Select(m => m.ToString())), r.PreparedByName))
            .ToList();

        return new DeliveryReportResultDto(
            new PagedResult<DeliveryReportRowDto>(items, rows.Page, rows.PageSize, rows.TotalCount, rows.TotalPages),
            totals);
    }

    private sealed record DeliveryReportRow(
        Guid DeliveryReceiptId, Guid SaleId, string SaleNumber, DateTime CreatedAtUtc,
        string RecipientName, string DeliveryAddress, string? ContactNumber, string? DeliveryNotes,
        decimal DeliveryCharge, decimal SaleGrandTotal, string PreparedByName, List<PaymentMethod> Methods);
```

- [ ] **Step 5: Implement the controller endpoint**

In `src/Negosio.Api/Controllers/ReportsController.cs`, add after the `Categories` action:

```csharp
    [HttpGet("deliveries")]
    [ProducesResponseType(typeof(DeliveryReportResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryReportResultDto>> Deliveries(
        [FromQuery] DeliveryReportQuery query, CancellationToken cancellationToken)
        => Ok(await _reports.GetDeliveriesAsync(query, cancellationToken));
```

No new authorization attribute needed — the class-level `[Authorize(Policy = AuthorizationPolicies.ReportsView)]` already covers every action on this controller.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "DeliveryReportTests|ReportsTests"`
Expected: PASS — the 5 new tests plus every pre-existing report test (proves the new `IReportsService` method didn't disturb the other three).

If a helper method signature (`CreateBranchAsync`, `AddTenantUserTokenAsync`, etc.) doesn't match exactly what's in `tests/Negosio.IntegrationTests/Infrastructure/`, adjust the test to the real signature — these were written from strong precedent in sibling test files, not guessed, but confirm against the actual helper class before assuming the brief is wrong.

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Application/Reports/ReportsContracts.cs src/Negosio.Application/Reports/ReportsService.cs src/Negosio.Api/Controllers/ReportsController.cs tests/Negosio.IntegrationTests/Reports/DeliveryReportTests.cs
git commit -m "feat(reports): add the Delivery Report (DeliveryReceipt joined to Sale)"
```

---

### Task 15: Frontend — Delivery Reports page

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/api/reports.ts`
- Create: `web/negosio-web/src/pages/DeliveryReportsPage.tsx`
- Modify: `web/negosio-web/src/App.tsx`
- Modify: `web/negosio-web/src/lib/nav.ts`

**Interfaces:**
- Consumes: `GET /api/reports/deliveries` → `DeliveryReportResultDto` from Task 14; `formatDeliveryCharge`/`formatMoney` from Task 7/existing `lib/format.ts`; `usePagedQuery` (existing, unchanged).
- Produces: the `/reports/delivery` route — a self-contained page, nothing later depends on it.

No automated test for this task (no frontend test runner exists in this repo) — verified by `tsc`/`build` here and folded into Task 16's live pass.

- [ ] **Step 1: Add the TypeScript types**

In `web/negosio-web/src/api/types.ts`, add near the other Reports types (after `ReportFilterParams`):

```ts
export interface DeliveryReportRowDto {
  deliveryReceiptId: string
  saleId: string
  saleNumber: string
  createdAtUtc: string
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  deliveryNotes: string | null
  deliveryCharge: number
  saleGrandTotal: number
  paymentSummary: string
  preparedByName: string
}

export interface DeliveryReportTotalsDto {
  totalDeliveries: number
  freeDeliveries: number
  chargedDeliveries: number
  totalDeliveryCharges: number
  averageDeliveryCharge: number
}

export interface DeliveryReportResultDto {
  page: PagedResult<DeliveryReportRowDto>
  totals: DeliveryReportTotalsDto
}

export interface DeliveryReportParams {
  branchId?: string
  fromUtc?: string
  toUtc?: string
  search?: string
  page?: number
  pageSize?: number
}
```

- [ ] **Step 2: Add the API client function**

Replace `web/negosio-web/src/api/reports.ts` in full:

```ts
import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  CategoryPerformanceDto,
  DeliveryReportParams,
  DeliveryReportResultDto,
  ReportFilterParams,
  ReportsOverviewDto,
  TopProductDto,
} from './types'

export const reportsApi = {
  overview: (params: ReportFilterParams) =>
    apiRequest<ReportsOverviewDto>(`/api/reports/overview${qs({ ...params })}`),

  topProducts: (params: ReportFilterParams, top: number) =>
    apiRequest<TopProductDto[]>(`/api/reports/top-products${qs({ ...params, top })}`),

  categories: (params: ReportFilterParams) =>
    apiRequest<CategoryPerformanceDto[]>(`/api/reports/categories${qs({ ...params })}`),

  deliveries: (params: DeliveryReportParams) =>
    apiRequest<DeliveryReportResultDto>(`/api/reports/deliveries${qs({ ...params })}`),
}
```

- [ ] **Step 3: Create the page**

Create `web/negosio-web/src/pages/DeliveryReportsPage.tsx` — modeled directly on `SalesPage.tsx`'s filter bar / `usePagedQuery` / `Table`/`Pagination` structure, with a `MetricCard` summary row on top (the `ReportsPage.tsx` idiom) for the totals:

```tsx
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Banknote, Gift, PackageCheck, Percent, Truck } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { reportsApi } from '../api/reports'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { formatDeliveryCharge, formatMoney } from '../lib/format'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  EmptyState,
  ErrorState,
  MetricCard,
  Pagination,
  SearchInput,
  Select,
  SkeletonCard,
  SkeletonText,
  Table,
} from '../components/ui'

type Filters = {
  from: string | undefined
  to: string | undefined
  branchId: string | undefined
}

const DEFAULT_FILTERS: Filters = { from: undefined, to: undefined, branchId: undefined }

export default function DeliveryReportsPage() {
  const q = usePagedQuery<Filters>({ defaultFilters: DEFAULT_FILTERS })

  const branchesQuery = useQuery({
    queryKey: ['branches', 'delivery-reports-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const branches = branchesQuery.data ?? []
  const multiBranch = branches.length > 1

  const query = useQuery({
    queryKey: [
      'reports',
      'deliveries',
      { page: q.page, search: q.search, from: q.filters.from, to: q.filters.to, branchId: q.filters.branchId },
    ],
    queryFn: () =>
      reportsApi.deliveries({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        branchId: q.filters.branchId,
        fromUtc: q.filters.from ? new Date(`${q.filters.from}T00:00:00.000`).toISOString() : undefined,
        toUtc: q.filters.to ? new Date(`${q.filters.to}T23:59:59.999`).toISOString() : undefined,
      }),
  })

  const filtered = Boolean(q.search || q.filters.from || q.filters.to || q.filters.branchId)
  const totals = query.data?.totals

  const header = (
    <Table.Head>
      <Table.HeaderCell>Date</Table.HeaderCell>
      <Table.HeaderCell>Sale #</Table.HeaderCell>
      <Table.HeaderCell>Recipient</Table.HeaderCell>
      <Table.HeaderCell>Address</Table.HeaderCell>
      <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
      <Table.HeaderCell align="right">Sale total</Table.HeaderCell>
      <Table.HeaderCell>Payment</Table.HeaderCell>
      <Table.HeaderCell>Prepared by</Table.HeaderCell>
    </Table.Head>
  )
  const colCount = 8

  return (
    <DashboardLayout title="Delivery Reports">
      <div className="space-y-5">
        <h1 className="text-2xl font-bold text-text-primary">Delivery Reports</h1>

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          {query.isPending ? (
            Array.from({ length: 5 }).map((_, i) => <SkeletonCard key={i} />)
          ) : (
            <>
              <MetricCard icon={Truck} accent="blue" label="Total deliveries" value={totals?.totalDeliveries ?? 0} />
              <MetricCard icon={Gift} accent="green" label="Free deliveries" value={totals?.freeDeliveries ?? 0} />
              <MetricCard icon={PackageCheck} accent="purple" label="Charged deliveries" value={totals?.chargedDeliveries ?? 0} />
              <MetricCard
                icon={Banknote}
                accent="amber"
                label="Total charges collected"
                value={formatMoney(totals?.totalDeliveryCharges ?? 0)}
              />
              <MetricCard
                icon={Percent}
                accent="red"
                label="Average delivery charge"
                value={formatMoney(totals?.averageDeliveryCharge ?? 0)}
              />
            </>
          )}
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search by sale #, recipient, address, or contact number"
          />
          {multiBranch && (
            <Select
              aria-label="Branch"
              className="sm:max-w-[12rem]"
              value={q.filters.branchId ?? ''}
              onChange={(e) => q.setFilter('branchId', e.target.value || undefined)}
            >
              <option value="">All branches</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                  {b.isActive ? '' : ' (inactive)'}
                </option>
              ))}
            </Select>
          )}
          <input
            type="date"
            aria-label="From date"
            value={q.filters.from ?? ''}
            onChange={(e) => q.setFilter('from', e.target.value || undefined)}
            className="h-11 rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
          <input
            type="date"
            aria-label="To date"
            value={q.filters.to ?? ''}
            onChange={(e) => q.setFilter('to', e.target.value || undefined)}
            className="h-11 rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            {header}
            <Table.Body>
              {Array.from({ length: 6 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: colCount }).map((__, j) => (
                    <Table.Cell key={j}>
                      <SkeletonText className={j === 0 ? 'w-24' : 'w-20'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.page.items.length === 0 ? (
          <EmptyState
            icon={Truck}
            title={filtered ? 'No deliveries match these filters' : 'No deliveries yet'}
            description={
              filtered
                ? 'Try clearing the search or date range.'
                : 'Sales marked "For delivery" in the POS will show here.'
            }
          />
        ) : (
          <>
            <Table>
              {header}
              <Table.Body>
                {query.data.page.items.map((r) => (
                  <Table.Row key={r.deliveryReceiptId}>
                    <Table.Cell>{new Date(r.createdAtUtc).toLocaleString()}</Table.Cell>
                    <Table.Cell>
                      <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                        #{r.saleNumber}
                      </Link>
                    </Table.Cell>
                    <Table.Cell>{r.recipientName}</Table.Cell>
                    <Table.Cell className="max-w-xs truncate">{r.deliveryAddress}</Table.Cell>
                    <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                    <Table.Cell align="right" className="font-semibold text-text-primary">
                      {formatMoney(r.saleGrandTotal)}
                    </Table.Cell>
                    <Table.Cell>{r.paymentSummary}</Table.Cell>
                    <Table.Cell>{r.preparedByName}</Table.Cell>
                  </Table.Row>
                ))}
              </Table.Body>
            </Table>
            <Pagination
              page={query.data.page.page}
              pageSize={query.data.page.pageSize}
              totalCount={query.data.page.totalCount}
              totalPages={query.data.page.totalPages}
              onPageChange={q.setPage}
            />
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
```

If any of the `lucide-react` icon names (`Gift`, `PackageCheck`, `Percent`, `Truck`, `Banknote`) don't exist in the installed version, substitute the closest available icon — this is a cosmetic choice, not a functional one.

- [ ] **Step 4: Register the route**

In `web/negosio-web/src/App.tsx`, add `import DeliveryReportsPage from './pages/DeliveryReportsPage'` alongside the other page imports, and add this route entry immediately after the existing `/reports` entry:

```tsx
  {
    path: '/reports/delivery',
    element: (
      <RequireCapability capability="reports:view" title="Delivery Reports">
        <DeliveryReportsPage />
      </RequireCapability>
    ),
  },
```

- [ ] **Step 5: Add the nav entry**

In `web/negosio-web/src/lib/nav.ts`, add `Truck` to the `lucide-react` import list at the top of the file, and add this item right after the existing `Reports` entry in the same `NavGroup`:

```ts
      { label: 'Delivery Reports', icon: Truck, to: '/reports/delivery', enabled: true, capability: 'reports:view' },
```

- [ ] **Step 6: Run TypeScript + build**

Run: `cd web/negosio-web && npx tsc --noEmit && npm run build`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add web/negosio-web/src/api/types.ts web/negosio-web/src/api/reports.ts web/negosio-web/src/pages/DeliveryReportsPage.tsx web/negosio-web/src/App.tsx web/negosio-web/src/lib/nav.ts
git commit -m "feat(reports): add the Delivery Reports page"
```

---

### Task 16: Live verification pass

**Files:** none — manual verification only (no frontend test runner exists in this repo, matching how the original "For delivery" feature was verified last session per `docs/handover.md`).

- [ ] **Step 1: Start the dev servers**

```bash
dotnet run --project src/Negosio.Api   # :5170
```
```bash
cd web/negosio-web && npm run dev       # :5173
```

- [ ] **Step 2: Walk through every scenario from the spec's "Required tests" list, live, in the browser**

1. Normal sale, "For delivery" unchecked: checkout, confirm the request has no delivery charge shown/entered, no Delivery Receipt created, total unchanged from before this feature.
2. Paid delivery with a charge > 0 (e.g. ₱60): confirm the "Amount due" banner, cash-received suggestions, and change all reflect subtotal + tax + ₱60; confirm payment; confirm the Payment-Successful screen's "Delivery charge" row shows ₱60.00; open the printed delivery receipt and confirm the "Delivery fee" line shows ₱60.00; open the sale detail page and confirm the "Delivery charge" row shows ₱60.00 and Total includes it.
3. Free delivery (charge left at 0.00, "For delivery" checked): checkout succeeds, DR is created, printed delivery receipt shows "Delivery fee: Free", sale total equals the pre-delivery total.
4. Type a negative value into the delivery-charge field (if the browser's number input allows it) or otherwise force one via devtools: confirm an inline error appears and Confirm payment is blocked; separately, confirm a crafted request with a negative `deliveryCharge` sent directly to `POST /api/pos/checkout` gets a 400.
5. Type a 3-decimal value (e.g. "10.005"): confirm the same inline error and 400 behavior as #4.
6. Uncheck "For delivery" after typing a charge: confirm the field disappears and resets; re-check it and confirm it shows "0.00" again, not the previously typed value.
7. Force a checkout failure (e.g. quantity > stock) with "For delivery" checked and a non-zero charge entered: confirm the checkbox, delivery fields, AND the delivery charge all survive the payment modal closing/reopening; fix the issue and retry; confirm exactly one Sale and one Delivery Receipt are created, and the charge is applied exactly once (check via the sale detail page's Total).
8. With a valid delivery sale, confirm via the existing "DR creation failure" toast path from the prior session's testing that a rejected delivery-receipt attempt never touches the Sale; independently confirm via the API/sale detail page that `deliveryCharge` is still persisted on that Sale.
9. Confirm an ordinary (non-delivery) sale made before this feature still displays correctly on its existing sale detail/receipt pages (no extra "Delivery charge" row, since it's 0), and does **not** appear in the new Delivery Report.
10. Open **Delivery Reports** (`/reports/delivery`, or via the sidebar): confirm the summary tiles (total/free/charged deliveries, total charges, average charge) match what you'd expect from the deliveries created in steps 2-3 plus any from earlier sessions; confirm searching by sale number, recipient name, address, and contact number each narrow the table correctly; confirm newest-first ordering; confirm a Cashier account gets 403/is blocked from the page (`useCan('reports:view')` hides the nav link, and the API itself returns 403).
11. `npx tsc --noEmit` and `npm run build` both clean (already run in Tasks 13/15 — re-run once more here as the final gate).
12. Zero JS console errors across the above.
13. Re-check layout at 1366×768 and 1920×1080 in the Take-payment modal with the delivery section expanded (one more field taller than before — confirm the modal's existing internal scroll (`max-h-[62vh] overflow-y-auto`) still keeps Cancel/Confirm reachable), and in the new Delivery Reports table (confirm it scrolls horizontally on its own rather than the page, per this app's responsive-table convention).

- [ ] **Step 3: Report results**

No commit for this task. If any scenario fails, fix forward in the task above it owns that behavior, then re-run the full scenario list before declaring the branch ready for review.

---

## Summary of what to tell the user when done

When every task above is green, report back with:
- **Files changed** — the full list across backend, frontend, and the new Delivery Report.
- **Database migration created** — the one new migration (`AddSaleDeliveryCharge`), the single `Sales.DeliveryCharge` column, and the `defaultValue: 0m` detail (why it matters for existing tenant rows — every pre-existing sale reads `0` after the migration, with no data loss).
- **Final-total calculation changes** — `GrandTotal = saleTotal + DeliveryCharge`, delivery charge validated identically front/back (`>= 0`, ≤ 2 decimals, no upper cap per existing convention), applied exactly once (proven by the checkout idempotency test and the DR-retry test), never taxed or discounted.
- **Delivery report implementation** — `GET /api/reports/deliveries` on the existing `ReportsController`/`IReportsService` (no new controller/service), `DeliveryReceipt` joined to `Sale` (no new table, no duplicated `SaleNumber`/`DeliveryCharge`), gated behind the existing `reports:view` capability, paged + searchable + newest-first, with full-filtered-set summary totals.
- **Calculation/design decisions** — `DeliveryCharge` lives only on `Sale`, never duplicated onto `DeliveryReceipt` (Task 4); the two deliberate display-gating trade-offs (Tasks 11/12 gate on "a DR exists"; Task 13 gates on "`> 0`", documented there as a known limitation for the free-delivery-on-plain-receipts edge case).
- **Tests executed and results** — the `dotnet test` run (Task 6, and again after Task 14) and the `tsc`/`build`/manual pass (Task 16).
- **Assumptions / unresolved issues** — the Task 13 gating trade-off above, and anything Task 16 surfaced.

Do not merge or push — the branch stays `feature/pos-for-delivery`, awaiting the user's review, per the existing project convention for this branch.
