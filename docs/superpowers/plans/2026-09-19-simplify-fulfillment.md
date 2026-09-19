# Simplify Fulfillment to Whole-Sale Level — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the per-item, multi-schedule, partial-allocation fulfillment system (built by the 2026-09-13 plan) with a whole-Sale-level model: exactly one of Take now / For delivery / For pickup, chosen once in the Payment modal, applying to 100% of every Sale item.

**Architecture:** Keep every domain entity, table, and migration exactly as they are — `SaleItem.DeliveryRequiredQuantity`/`PickupRequiredQuantity`, `DeliveryReceipt`/`DeliveryReceiptItem`, `FulfillmentConversion` were already general enough to express "100% of this line goes to one method," they just also happened to support partial values. This plan constrains the **application layer** (checkout, schedule creation, validators) to only ever produce whole-sale allocations, and deletes the machinery that existed solely to support partial/multi-schedule state: the batch-create endpoints, the cross-schedule availability ledger, the per-item breakdown DTO, and the granular combined-allocation report. Cancellation/conversion logic needs **no changes** — it already builds a replacement from the cancelled schedule's own full item list, which will now always equal 100% of the sale by construction. Nothing is deployed anywhere (confirmed: this whole branch, including the original Delivery-only feature layered on top of the already-`master`-merged Receipt Settings work, is unmerged), so contract-shape changes are free of migration risk.

**Tech Stack:** Backend .NET 9 / ASP.NET Core Web API / EF Core 9 / SQL Server (LocalDB), modular monolith. Frontend React 19 / TypeScript strict / Vite / TanStack Query v5 / Tailwind v4. No frontend test runner — `npx tsc -b` and `npm run build` are the only frontend checks.

**Spec:** `docs/superpowers/specs/2026-09-19-simplify-fulfillment.md` (this plan argues from that spec; where the two disagree, the spec wins). For historical context on what is being simplified, see `docs/superpowers/specs/2026-09-13-pickup-fulfillment.md` and `docs/superpowers/reports/2026-09-13-pickup-fulfillment-final-report.md`.

## Global Constraints

- **Do not merge or push.** Stated twice by the user.
- **No migration changes.** No new migration, no edit to an existing one. Confirmed safe: every entity already supports whole-sale allocation as a special case of what it already stores.
- **No frontend test runner** — `npx tsc -b` (never a bare `--noEmit`, which silently checks zero files due to TS project references) and `npm run build` are the only frontend checks.
- **HTTP convention**: `POST /{id}/{verb}` for state-changing actions.
- A sale may have **at most one active (non-Cancelled) `DeliveryReceipt`, of either method, at any time** — enforced server-side, never trusted from the client.
- Every new Delivery/Pickup schedule always covers **every Sale item at its full sold quantity** — there is no client-supplied item list anymore.
- Delivery charge: stored once on `Sale`, added once to the total, must be `0` whenever the active method is not Delivery (validated server-side, not just normalized client-side).
- **Correction found during Task 3 (see ledger for the full discovery):** this plan originally assumed cancellation/conversion behavior was entirely unchanged. That's true for `ConvertToPickup`/`CustomerPickedUpInstead`/`ConvertToDelivery` (they already build a replacement of the *opposite* method, matching the spec exactly) — but the spec's own "Deliver later"/"Pickup later" sections require creating **a new Pending replacement of the SAME method**, which the pre-existing implementation does not do (`CancelDeliveryAsync`/`CancelPickupAsync` currently call `RejectUnwantedReplacement` and pass `buildReplacement: null` for these two dispositions — a pure release, no replacement). Task 3B fixes this. `DeliveryReceipt.Cancel`, `SaleItem.ConvertFulfillment`, `FulfillmentConversion`, and `CancelWithDispositionAsync`'s replacement-building loop itself (`DeliveryReceiptService.cs`, the `CancelWithDispositionAsync` method) genuinely need **zero changes** even for this fix — they already branch correctly on whether `buildReplacement` is null, so passing a real factory for `DeliverLater`/`PickupLater` instead of `null` is sufficient. The fix is confined to `CancelDeliveryAsync`/`CancelPickupAsync`'s switch-case bodies, the two cancel-request contract shapes, and their validators.

---

### Task 1: Backend — whole-sale `FulfillmentMethod` at checkout

**Files:**
- Modify: `src/Negosio.Application/Pos/CheckoutContracts.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutValidator.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutService.cs`
- Test: `tests/Negosio.UnitTests/Sales/SaleItemFulfillmentTests.cs` (or wherever `Sale.AddItem` fulfillment-quantity tests currently live — grep for `DeliveryRequiredQuantity` first)
- Test: `tests/Negosio.IntegrationTests/Pos/CheckoutAllocationTests.cs`, `CheckoutDeliveryFulfillmentTests.cs`

**Interfaces:**
- Consumes: `Negosio.Domain.Enums.FulfillmentMethod` (already exists: `TakeNow`/`Delivery`/`Pickup`), `Sale.AddItem(..., decimal deliveryRequiredQuantity, decimal pickupRequiredQuantity)` (unchanged signature, `src/Negosio.Domain/Entities/Sales/Sale.cs`).
- Produces: `CheckoutRequest.Method: FulfillmentMethod` — every later task that reads what the cashier chose reads this field. `CheckoutItemInput` no longer carries `DeliveryRequiredQuantity`/`PickupRequiredQuantity`.

- [ ] **Step 1: Simplify `CheckoutContracts.cs`**

Remove `DeliveryRequiredQuantity`/`PickupRequiredQuantity` from `CheckoutItemInput` (now just `ProductVariantId`, `Quantity`, `Discount`). Add `FulfillmentMethod Method = FulfillmentMethod.TakeNow` to `CheckoutRequest`, right after `Payments`:

```csharp
public sealed record CheckoutItemInput(
    Guid ProductVariantId,
    decimal Quantity,
    CheckoutDiscountInput? Discount);

public sealed record CheckoutRequest(
    Guid BranchId,
    Guid RegisterSessionId,
    Guid ClientRequestId,
    IReadOnlyList<CheckoutItemInput> Items,
    IReadOnlyList<CheckoutPaymentInput> Payments,
    /// <summary>Whole-sale fulfillment choice — applies to every item at its full quantity.
    /// Defaults to TakeNow so an older client that never sends this field keeps working.</summary>
    FulfillmentMethod Method = FulfillmentMethod.TakeNow,
    /// <summary>The delivery fee — must be 0 unless <see cref="Method"/> is Delivery (validated,
    /// never silently coerced server-side).</summary>
    decimal DeliveryCharge = 0m,
    VoidSaleApprovalInput? Approval = null);
```

`SaleResultItemDto` keeps `DeliveryRequiredQuantity` (still useful — it now always equals either `0` or `Quantity`, and the frontend's post-checkout schedule POST reads each result item's own quantity directly rather than this field, but nothing requires removing it and other code may already read it — leave it).

- [ ] **Step 2: Simplify `CheckoutValidator.cs`**

Replace the per-item delivery/pickup rules with a `Method` enum check and a charge/method cross-check:

```csharp
RuleForEach(x => x.Items).ChildRules(item =>
{
    item.RuleFor(i => i.ProductVariantId).NotEmpty().WithMessage("Each item needs a product variant.");
    item.RuleFor(i => i.Quantity).GreaterThan(0).WithMessage("Item quantity must be greater than zero.");
});
RuleFor(x => x.Payments).NotEmpty().WithMessage("At least one payment is required.");
RuleFor(x => x.Method).IsInEnum().WithMessage("Fulfillment method must be TakeNow, Delivery, or Pickup.");
RuleFor(x => x.DeliveryCharge)
    .GreaterThanOrEqualTo(0m).WithMessage("Delivery charge cannot be negative.")
    .Must(v => v == Math.Round(v, 2, MidpointRounding.AwayFromZero))
    .WithMessage("Delivery charge can have at most 2 decimal places.")
    .Equal(0m).WithMessage("Delivery charge must be 0 unless the fulfillment method is Delivery.")
        .When(x => x.Method != FulfillmentMethod.Delivery);
```

(Import `Negosio.Domain.Enums` if not already imported.)

- [ ] **Step 3: Update `CheckoutService.CheckoutAsync`**

Find where each `SaleItem` is added to the `Sale` (search for `sale.AddItem(` in `CheckoutService.cs`). It currently passes `item.DeliveryRequiredQuantity`/`item.PickupRequiredQuantity` straight from the request. Change it to compute both from `request.Method`:

```csharp
var deliveryRequiredQuantity = request.Method == FulfillmentMethod.Delivery ? item.Quantity : 0m;
var pickupRequiredQuantity = request.Method == FulfillmentMethod.Pickup ? item.Quantity : 0m;
sale.AddItem(..., deliveryRequiredQuantity, pickupRequiredQuantity);
```

Read the surrounding method fully first — there is existing logic for pricing/discount/tax per line that must stay untouched; only the two fulfillment-quantity arguments change from client-supplied to server-computed.

- [ ] **Step 4: Update existing tests, then add new ones**

Grep the whole `tests/` tree for `DeliveryRequiredQuantity` and `PickupRequiredQuantity` used on `CheckoutItemInput` specifically (not on `SaleItem`/`SaleResultItemDto`, which are unaffected) and fix each construction site to use the new shape (no per-item quantities; set `Method` on the request instead).

Add (or confirm covered, citing the existing test if so) — these map to the spec's Backend acceptance criteria #1 and #6:

```csharp
[Fact]
public async Task TakeNow_checkout_leaves_every_item_at_zero_delivery_and_pickup_quantity()
{
    // Checkout with Method = TakeNow (the default — omit it) and assert every created SaleItem has
    // DeliveryRequiredQuantity == 0 and PickupRequiredQuantity == 0.
}

[Fact]
public async Task Delivery_checkout_sets_every_item_delivery_required_quantity_to_its_full_quantity()
{
    // Checkout with Method = Delivery, two lines of different quantities. Assert BOTH items have
    // DeliveryRequiredQuantity == Quantity and PickupRequiredQuantity == 0 — "applies to every item,
    // not just one" is the thing worth asserting here.
}

[Fact]
public async Task Pickup_checkout_sets_every_item_pickup_required_quantity_to_its_full_quantity()
{
    // Mirror of the above for Pickup.
}

[Fact]
public async Task Delivery_charge_above_zero_is_rejected_when_method_is_not_delivery()
{
    // Method = TakeNow (or Pickup), DeliveryCharge = 50 -> 400 ValidationAppException. This is the
    // "backend must not trust only the frontend selection" requirement — the validator, not just the
    // UI, is what makes this impossible.
}
```

- [ ] **Step 5: Run and verify**

Run: `dotnet test tests/Negosio.UnitTests tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Checkout"`
Expected: all pass. Fix any compile break elsewhere in the solution that still constructs `CheckoutItemInput` with the removed fields (search-wide, not just in tests — check `src/` too, e.g. any seed/demo data helper).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(pos): whole-sale FulfillmentMethod replaces per-item delivery/pickup quantities at checkout"
```

---

### Task 2: Backend — simplify schedule creation (drop batch/Items, one-active-schedule invariant)

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptValidators.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs`
- Modify: `src/Negosio.Api/Controllers/SalesController.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptCreateTests.cs`, `PickupTests.cs`, `DeliveryReceiptConcurrencyTests.cs`

**Interfaces:**
- Consumes: Task 1's `SaleItem.DeliveryRequiredQuantity`/`PickupRequiredQuantity` (now always `0` or the full line quantity).
- Produces: `CreateDeliveryReceiptRequest`/`CreatePickupRequest` with no `Items` field — every later frontend task builds requests against this shape. `IDeliveryReceiptService` loses `CreateDeliveryBatchAsync`/`CreatePickupBatchAsync`.

This is the task with the most deletion. Read `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` in full before starting — it is ~1080 lines and this task touches roughly a third of it.

- [ ] **Step 1: Simplify `DeliveryReceiptContracts.cs`**

Remove `IReadOnlyList<FulfillmentItemInput> Items` from `CreateDeliveryReceiptRequest` and `CreatePickupRequest`. Delete entirely: `CreateDeliveryReceiptBatchRequest`, `CreatePickupBatchRequest`, `FulfillmentBatchResultDto`. `FulfillmentItemInput` itself stays — it is still used by `PickupReplacementInput`... actually check: `PickupReplacementInput`/`DeliveryReplacementInput` never carried an `Items` field (confirmed by reading the file — replacement items are always copied from the cancelled schedule's own `Items`, never client-supplied), so `FulfillmentItemInput` becomes entirely unused once `Items` is removed from both create requests. Delete `FulfillmentItemInput` too, and its validator `FulfillmentItemInputValidator` in the validators file.

In `IDeliveryReceiptService`, delete `CreateDeliveryBatchAsync`/`CreatePickupBatchAsync` from the interface.

- [ ] **Step 2: Simplify `DeliveryReceiptValidators.cs`**

In `CreateDeliveryReceiptRequestValidator` and `CreatePickupRequestValidator`, delete the three `Items`-related rules (`NotEmpty`, `RuleForEach(...).SetValidator(...)`, the duplicate-`SaleItemId` check). Delete `CreateDeliveryReceiptBatchRequestValidator`, `CreatePickupBatchRequestValidator`, and `FulfillmentItemInputValidator` entirely.

- [ ] **Step 3: Simplify `DeliveryReceiptService.cs` — delete the batch/availability machinery**

Delete these methods entirely: `CreateDeliveryBatchAsync`, `CreatePickupBatchAsync`, `CreateScheduleBatchAsync`, `TryGetExistingBatchAsync`, `ComputeAvailabilityAsync`, `ValidateAndResolveLines`. Delete the `AvailabilityMap`/`MethodTotals` types wherever they are defined (check the bottom of this file or a sibling file in the same folder — grep `class AvailabilityMap` / `record MethodTotals`). Delete the `ScheduleInput.Items` field (the record stays — it is still used by `CreateScheduleAsync` for the flat schedule fields — but drop the `Items`/`FulfillmentItemInput` parts of it and of its two `From(...)` factory methods).

- [ ] **Step 2: Simplify `CreateScheduleAsync`**

Read the current implementation (`DeliveryReceiptService.cs:121-164` as of this plan's writing — line numbers may have shifted after Step 1's deletions, so locate it by name). Replace the availability/line-resolution logic with:

```csharp
private async Task<FulfillmentScheduleDto> CreateScheduleAsync(
    Guid saleId, FulfillmentMethod method, ScheduleInput schedule, CancellationToken ct)
{
    var tenantId = RequireTenant();

    var sale = await LoadFulfillableSaleAsync(tenantId, saleId, ct);
    EnsureNotPastBusinessToday(schedule.ScheduledDate, method);

    await using var transaction = await _db.Database.BeginTransactionAsync(ct);

    // Same lock every allocating/converting path takes — see LockSaleItemsAsync's doc comment.
    var lockedItems = await LockSaleItemsAsync(tenantId, sale.Id, ct);

    // One active (non-Cancelled) schedule per sale, of EITHER method, ever — this is the whole
    // simplified invariant. A stale pre-lock read would miss a schedule someone just created while
    // we waited on the lock, so this check runs after LockSaleItemsAsync, not before.
    var hasActiveSchedule = await _db.DeliveryReceipts.AsNoTracking()
        .AnyAsync(d => d.TenantId == tenantId && d.SaleId == saleId && d.Status != FulfillmentStatus.Cancelled, ct);
    if (hasActiveSchedule)
    {
        await transaction.RollbackAsync(ct);
        throw new BusinessRuleException(
            ErrorCodes.DeliveryReceiptNotAllowed,
            "This sale already has an active delivery or pickup. Cancel it first if you need to change the fulfillment method.");
    }

    // Every item earmarked for this method, at its full quantity — checkout already set this to
    // 0-or-full per Task 1, so there is nothing left to validate here; this is a direct read, not a
    // client-supplied selection.
    var quantityColumn = method == FulfillmentMethod.Delivery
        ? (Func<SaleItem, decimal>)(i => i.DeliveryRequiredQuantity)
        : i => i.PickupRequiredQuantity;
    var lines = lockedItems.Where(i => quantityColumn(i) > 0m).Select(i => (SaleItem: i, Quantity: quantityColumn(i))).ToList();
    if (lines.Count == 0)
    {
        await transaction.RollbackAsync(ct);
        throw new BusinessRuleException(
            ErrorCodes.DeliveryReceiptNotAllowed,
            $"This sale has no items earmarked for {MethodNoun(method)}.");
    }

    var preparedByName = await ResolvePreparedByNameAsync(ct);
    var sequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, method, ct);

    var dr = BuildSchedule(tenantId, sale, method, sequenceNumber, schedule, preparedByName, batchRequestId: null);

    foreach (var (saleItem, quantity) in lines)
    {
        dr.AddItem(saleItem.Id, saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, quantity, saleItem.UnitPrice);
    }

    _db.DeliveryReceipts.Add(dr);

    try
    {
        await _db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
    {
        await transaction.RollbackAsync(ct);
        throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
            "Another schedule was created for this sale at the same time. Please retry.");
    }

    await transaction.CommitAsync(ct);
    return await MapToDtoAsync(dr, ct);
}
```

This drops the `IsInEnum`/per-item cap logic entirely (nothing left to cap against — a schedule either can be created or can't, there's no partial case). It also replaces the removed `TryGetExistingBatchAsync` idempotency dance with no replacement at all, by design: the `hasActiveSchedule` guard above already makes a naive retry safe without a second idempotency layer — a retry that arrives after the first request committed simply hits that guard and gets a clear "already has an active delivery" error instead of silently duplicating. `BuildSchedule`'s `batchRequestId` parameter is always passed `null` from this simplified path (the column stays in the schema — untouched, no migration — and remains meaningful only on historical rows created by the old batch endpoints before this plan).

- [ ] **Step 4: Update `IDeliveryReceiptService.CreateDeliveryAsync`/`CreatePickupAsync` call sites**

Both already delegate to `CreateScheduleAsync` — just confirm they still compile after `ScheduleInput.From(...)` drops `Items`.

- [ ] **Step 5: Update `SalesController.cs`**

Delete the two batch routes: `POST /api/sales/{id}/delivery-receipts/batch` and `POST /api/sales/{id}/pickups/batch` (search for `CreateDeliveryReceiptBatch`/method names containing `Batch`). Keep the two single-create routes unchanged.

- [ ] **Step 6: Update tests**

Delete every test that exercises the batch endpoints or asserts partial/multi-schedule availability math (grep `CreateDeliveryBatchAsync`, `CreatePickupBatchAsync`, `TryGetExistingBatch`, `AvailabilityMap`, multi-item `Items:` lists with more than one schedule). Keep and adapt the single-create concurrency tests (e.g. "two concurrent creates on the same sale, only one succeeds") to assert against the new `hasActiveSchedule` guard's error instead of the old availability-exceeded error.

Add, per the spec's backend acceptance criteria:

```csharp
[Fact]
public async Task Creating_a_pickup_when_an_active_delivery_already_exists_is_rejected()
{
    // Delivery checkout, create the Delivery schedule, then attempt CreatePickupAsync on the same
    // sale. Assert BusinessRuleException / 400, and that no Pickup row was created.
}

[Fact]
public async Task Creating_a_second_delivery_when_one_is_already_pending_is_rejected()
{
    // Same-method duplicate — covers "must not have multiple active Delivery schedules."
}

[Fact]
public async Task A_new_delivery_covers_every_item_on_the_sale_at_full_quantity()
{
    // Two-line Delivery sale, create the schedule, assert dr.Items has one row per sale item, each
    // at that item's full Quantity — not a subset.
}
```

- [ ] **Step 7: Run and verify**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Delivery|FullyQualifiedName~Pickup"`
Expected: all pass, zero references to deleted batch/availability types remain anywhere in `src/`/`tests/` (grep to confirm).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(delivery): one schedule per sale, whole-item allocation — drop batch/partial-availability machinery"
```

---

### Task 3: Backend — simplify `SaleFulfillmentStatus`, the calculator, and the sale-level summary

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`
- Modify: `src/Negosio.Application/Delivery/SaleFulfillmentCalculator.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` (`GetSaleFulfillmentAsync`)
- Test: `tests/Negosio.UnitTests/Delivery/SaleFulfillmentCalculatorTests.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptCreateTests.cs` (or wherever `GetSaleFulfillmentAsync` is exercised)

**Interfaces:**
- Consumes: Task 2's guarantee that a sale has at most one active schedule.
- Produces: `SaleFulfillmentStatus` (6 values), `SaleFulfillmentSummaryDto` (no more `Items`/`CanCreateDelivery`/`CanCreatePickup`) — every later frontend task (Sale Detail, Reports) reads this new shape.

- [ ] **Step 1: Replace `SaleFulfillmentStatus`**

In `DeliveryReceiptContracts.cs`, replace the 8-value enum with the spec's named 6:

```csharp
/// <summary>Whole-sale fulfillment status. A sale has at most one active (non-Cancelled) schedule
/// at any time (enforced by CreateScheduleAsync), so this never needs to express "partial" or
/// "awaiting both methods" — those were artifacts of the per-item/multi-schedule design this
/// replaces.</summary>
public enum SaleFulfillmentStatus
{
    /// <summary>No Delivery or Pickup was ever created for this sale — everything was Take now.</summary>
    TakeNow = 1,
    PendingDelivery = 2,
    Delivered = 3,
    PendingPickup = 4,
    Claimed = 5,
    /// <summary>The sale's most recent schedule is Cancelled with no active replacement — should not
    /// normally occur (every cancellation disposition creates one), but is the honest label for it
    /// rather than a crash if it ever does (e.g. data from before this simplification).</summary>
    CancelledOrReplaced = 6,
}
```

- [ ] **Step 2: Simplify `SaleFulfillmentSummaryDto`**

Delete `SaleItemFulfillmentDto` entirely. Simplify `SaleFulfillmentSummaryDto` to:

```csharp
public sealed record SaleFulfillmentSummaryDto(
    Guid SaleId,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    /// <summary>The sale's current active schedule (Pending or Completed), or null for a Take-now
    /// sale that has never had one. At most one of Deliveries/Pickups below is the "active" one at
    /// any time — this field exists so the frontend never has to search two lists to find it.</summary>
    FulfillmentScheduleDto? ActiveSchedule,
    /// <summary>All Delivery schedules ever created for this sale, active and cancelled, newest
    /// first — the sale's full delivery history.</summary>
    IReadOnlyList<FulfillmentScheduleDto> Deliveries,
    /// <summary>Same as Deliveries, for Pickup.</summary>
    IReadOnlyList<FulfillmentScheduleDto> Pickups,
    IReadOnlyList<FulfillmentConversionDto> Conversions);
```

(`CanCreateDelivery`/`CanCreatePickup` are gone — under the new model there is no "create an additional schedule" action to gate; the only ways a sale gets a schedule are checkout, or cancel+convert, both of which already enforce the one-active-schedule rule structurally.)

- [ ] **Step 3: Rewrite `SaleFulfillmentCalculator.Derive`**

Replace the quantity-weighted function with a direct state read:

```csharp
namespace Negosio.Application.Delivery;

/// <summary>Derives a sale's whole-sale <see cref="SaleFulfillmentStatus"/> from its current active
/// (or most recent) schedule. Pure and stateless — shared by the Sale-detail summary and the
/// Delivery report's sale-level view so the two can never disagree.</summary>
public static class SaleFulfillmentCalculator
{
    /// <param name="latestSchedule">The sale's most recent Delivery-or-Pickup schedule (by
    /// CreatedAtUtc), across both methods — or null if the sale never had one (pure Take-now).</param>
    public static SaleFulfillmentStatus Derive(FulfillmentMethod? method, FulfillmentStatus? status)
    {
        if (method is null) return SaleFulfillmentStatus.TakeNow;

        return (method, status) switch
        {
            (FulfillmentMethod.Delivery, FulfillmentStatus.Pending) => SaleFulfillmentStatus.PendingDelivery,
            (FulfillmentMethod.Delivery, FulfillmentStatus.Completed) => SaleFulfillmentStatus.Delivered,
            (FulfillmentMethod.Pickup, FulfillmentStatus.Pending) => SaleFulfillmentStatus.PendingPickup,
            (FulfillmentMethod.Pickup, FulfillmentStatus.Completed) => SaleFulfillmentStatus.Claimed,
            _ => SaleFulfillmentStatus.CancelledOrReplaced,
        };
    }
}
```

- [ ] **Step 4: Rewrite `GetSaleFulfillmentAsync`**

Read the current implementation in full first (`DeliveryReceiptService.cs`, search `GetSaleFulfillmentAsync`). Replace its body with: load the sale, load every `DeliveryReceipt` for the sale (both methods, `Include(d => d.Items)`, ordered by `CreatedAtUtc` descending), map each to a DTO via the existing `MapToDtoAsync`, split into `Deliveries`/`Pickups` by `Method`, find the active one (`Status != Cancelled`, there is at most one across both lists per Task 2's invariant) as `ActiveSchedule`, call `SaleFulfillmentCalculator.Derive(ActiveSchedule?.Method, ActiveSchedule?.Status)` for `FulfillmentStatus`, and reuse the existing `LoadConversionsAsync` call for `Conversions` unchanged. Delete the old per-item `availability`/`itemDtos` construction entirely.

- [ ] **Step 5: Rewrite `SaleFulfillmentCalculatorTests.cs`**

Replace the old quantity-based Facts with one per enum branch:

```csharp
[Fact]
public void No_schedule_derives_TakeNow()
{
    SaleFulfillmentCalculator.Derive(null, null).Should().Be(SaleFulfillmentStatus.TakeNow);
}

[Fact]
public void Pending_delivery_derives_PendingDelivery()
{
    SaleFulfillmentCalculator.Derive(FulfillmentMethod.Delivery, FulfillmentStatus.Pending)
        .Should().Be(SaleFulfillmentStatus.PendingDelivery);
}

// ... one Fact per remaining branch (Completed delivery -> Delivered; pending/completed pickup;
// Cancelled -> CancelledOrReplaced) — six Facts total, matching the six enum values.
```

- [ ] **Step 6: Update every other reference to the old enum/DTO shape**

Grep `SaleFulfillmentStatus.` and `SaleItemFulfillmentDto` across `src/` and `tests/` — fix every remaining compile break (there will be several: `ReportsService.cs`'s `GetDeliveryFulfillmentAsync` calls the old `Derive` signature — Task 4 handles that specific call site, but note it here so this task's own build isn't expected fully green until Task 4 lands; run `dotnet build` at the end of this task and confirm the *only* remaining errors are in `ReportsService.cs`/`ReportsContracts.cs`, nowhere else).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(delivery): simplify SaleFulfillmentStatus to six whole-sale states"
```

---

### Task 3B: Fix `DeliverLater`/`PickupLater` to create a replacement schedule, per spec

**Inserted mid-plan.** Discovered during Task 3's execution and independently confirmed by the
controller: the spec's "Cancellation and replacement behavior" section requires **every** cancel
disposition — including `DeliverLater`/`PickupLater`, not just the `ConvertTo*`/
`CustomerPickedUpInstead` ones — to end with exactly one active replacement schedule. Read the
spec's own text again if you want to confirm this yourself:

> **Deliver later**
> - Cancel the current Delivery.
> - Preserve the cancelled record and reason.
> - Create a new Pending Delivery covering the complete Sale.
> - Require a new valid delivery date.
> - Prefill recipient, address, contact, notes, and delivery charge where appropriate.

> **Pickup later**
> - Cancel the current Pickup.
> - Preserve the cancelled record and reason.
> - Create a new Pending Pickup covering the complete Sale.
> - Require a new expected pickup date.

The pre-existing (already-shipped-on-this-branch, pre-dating this whole simplification plan)
implementation does not do this: `CancelDeliveryAsync`/`CancelPickupAsync` currently call
`RejectUnwantedReplacement(...)` and pass `buildReplacement: null` for `DeliverLater`/`PickupLater`
— a pure release back to "unscheduled," with no new schedule. That was correct under the OLD
partial-allocation design (where "unscheduled but still earmarked" was a first-class, durable state
the cashier could act on later via a "Create delivery"/"Create pickup" button). Under this plan's
whole-sale model, that intermediate state and its UI trigger no longer exist (Task 10 deletes
`CreateFulfillmentScheduleModal.tsx` and its trigger buttons entirely) — so without this fix, a
`DeliverLater`/`PickupLater` cancellation would leave a sale with **no way to ever get a schedule
again**, silently reverting it to reporting as `TakeNow`. This is a genuine data-integrity gap the
plan's own later tasks (9, 10) would otherwise build on top of without noticing.

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptValidators.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs`
- Test: `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/{DeliveryReceiptConcurrencyTests,DeliveryReceiptCreateTests,DeliveryReceiptStatusTests,FulfillmentConversionTests,PickupTests}.cs`

**Interfaces:**
- Consumes: `PickupReplacementFactory`/`DeliveryReplacementFactory` (existing, unchanged — `DeliveryReceiptService.cs`), `RequireReplacement<T>` (existing, unchanged), `EnsureNotPastBusinessToday` (existing, unchanged), `PickupReplacementInput`/`DeliveryReplacementInput` (existing contract types, unchanged shape — reused, not duplicated).
- Produces: `CancelDeliveryRequest`/`CancelPickupRequest` each gain one new optional field. Task 6 (frontend types) and the not-yet-dispatched frontend cancellation-modal work (folded into this task — see Step 4) must match these exact new field names.

- [ ] **Step 1: Extend the two cancel-request contracts**

`CancelDeliveryRequest`'s existing `Replacement: PickupReplacementInput?` field is only ever the
right shape for `ConvertToPickup`/`CustomerPickedUpInstead` (both build a *Pickup*).
`DeliverLater` needs a *Delivery*-shaped replacement (it needs `DeliveryAddress`, which
`PickupReplacementInput` has no field for) — a genuinely different shape, so it needs its own
field, not a reuse of `Replacement`. In `DeliveryReceiptContracts.cs`:

```csharp
public sealed record CancelDeliveryRequest(
    string Reason,
    CancellationDisposition Disposition,
    /// <summary>Required for ConvertToPickup and CustomerPickedUpInstead — the pickup to create.
    /// Must be null for every other disposition.</summary>
    PickupReplacementInput? Replacement,
    /// <summary>Required for DeliverLater — the new Delivery to create, replacing the cancelled
    /// one. Must be null for every other disposition.</summary>
    DeliveryReplacementInput? RescheduledDelivery = null);

public sealed record CancelPickupRequest(
    string Reason,
    CancellationDisposition Disposition,
    /// <summary>Required for ConvertToDelivery — the delivery to create. Must be null for every
    /// other disposition.</summary>
    DeliveryReplacementInput? Replacement,
    /// <summary>Required for PickupLater — the new Pickup to create, replacing the cancelled one.
    /// Must be null for every other disposition.</summary>
    PickupReplacementInput? RescheduledPickup = null);
```

- [ ] **Step 2: Update the two cancel validators**

In `DeliveryReceiptValidators.cs`, `CancelDeliveryRequestValidator`: change the existing
`.Null().When(x => x.Disposition == CancellationDisposition.DeliverLater)` rule on `Replacement`
to stay as-is (still correct — `Replacement`, the Pickup-shaped field, must still be null for
`DeliverLater`), and add the mirror pair for the new field:

```csharp
RuleFor(x => x.RescheduledDelivery)
    .NotNull()
    .WithMessage("A new delivery's details are required for this disposition.")
    .When(x => x.Disposition == CancellationDisposition.DeliverLater);

RuleFor(x => x.RescheduledDelivery)
    .Null()
    .WithMessage("Rescheduled-delivery details must not be provided for this disposition.")
    .When(x => x.Disposition != CancellationDisposition.DeliverLater);

RuleFor(x => x.RescheduledDelivery!)
    .SetValidator(new DeliveryReplacementInputValidator())
    .When(x => x.RescheduledDelivery != null);
```

Mirror for `CancelPickupRequestValidator` with `RescheduledPickup`/`PickupLater`/
`PickupReplacementInputValidator`. `DeliveryReplacementInputValidator`/`PickupReplacementInputValidator`
already exist (used by the `ConvertTo*` paths) — reuse them, do not duplicate.

- [ ] **Step 3: Rewire `CancelDeliveryAsync`/`CancelPickupAsync`**

In `DeliveryReceiptService.cs`, change the `DeliverLater` case from a reject-and-release to a
require-and-replace, mirroring the `ConvertToPickup` case immediately below it:

```csharp
case CancellationDisposition.DeliverLater:
{
    var replacement = RequireReplacement(request.RescheduledDelivery);
    EnsureNotPastBusinessToday(replacement.ScheduledDate, FulfillmentMethod.Delivery);
    return await CancelWithDispositionAsync(
        id, FulfillmentMethod.Delivery, request.Reason, request.Disposition,
        convertToMethod: FulfillmentMethod.Delivery,
        completeReplacementImmediately: false,
        buildReplacement: DeliveryReplacementFactory(replacement), ct);
}
```

Mirror for `PickupLater` in `CancelPickupAsync`, using `PickupReplacementFactory(replacement)` and
`request.RescheduledPickup`. `RejectUnwantedReplacement` becomes unused after this change — delete
it (grep first to confirm no other call site).

**Do not touch** `CancelWithDispositionAsync` itself, `DeliveryReceipt.Cancel`,
`SaleItem.ConvertFulfillment`, or `FulfillmentConversion.Record` — `CancelWithDispositionAsync`
already branches correctly on whether `buildReplacement` is null (including the sale-status
"reject a replacement against a voided sale" guard, which `DeliverLater`/`PickupLater` now
correctly inherit for free simply by passing a non-null factory). Read that method once to confirm
this for yourself before concluding you need to change it — you should conclude you don't.

- [ ] **Step 4: Update every test that constructs a `DeliverLater`/`PickupLater` cancel request**

Grep `DeliverLater`/`PickupLater` across `tests/` (both projects). Every call site currently
passing `replacement: null` for one of these two dispositions and expecting success now needs a
real `RescheduledDelivery`/`RescheduledPickup` payload instead — construct one the same way the
neighboring `ConvertToPickup`/`ConvertToDelivery` test cases in the same file already do (same
`DeliveryReplacementInput`/`PickupReplacementInput` shape, a future date, a plausible
recipient/contact/notes). Any assertion that specifically checked "the released quantity has no
active schedule" / "still shows unscheduled" for these dispositions needs updating to instead
assert the new replacement schedule exists, is `Pending`, and covers every item at full quantity —
the same assertion pattern the `ConvertToPickup`/`ConvertToDelivery` tests in the same files
already use.

- [ ] **Step 5: Run and verify**

Run: `dotnet build` — 0 new errors (the pre-existing `ReportsService.cs` break from Task 3 is
still expected and not this task's concern). Run:
`dotnet test tests/Negosio.UnitTests tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Delivery|FullyQualifiedName~Pickup"`
— no regressions versus Task 3's own baseline (the same pre-existing `FulfillmentReportTests`
failures are expected and unrelated; anything else failing is this task's to fix).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "fix(delivery): DeliverLater/PickupLater now create a replacement schedule, per spec"
```

---

### Task 4: Backend — reports simplification (drop the combined per-allocation report)

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/FulfillmentReportTests.cs`, `ReportsTests.cs`

**Interfaces:**
- Consumes: Task 3's `SaleFulfillmentCalculator.Derive(FulfillmentMethod?, FulfillmentStatus?)` and `SaleFulfillmentStatus`.
- Produces: `DeliveryReportResultDto`/`PickupReportResultDto` (unchanged shape), `DeliveryFulfillmentReportResultDto` (unchanged shape, updated derivation). `FulfillmentReportResultDto`/`FulfillmentReportRowDto`/`FulfillmentReportQuery` are **deleted** — no later task produces or consumes them.

- [ ] **Step 1: Delete the combined per-allocation report**

In `ReportsContracts.cs`, delete: `FulfillmentReportRowDto`, `FulfillmentReportSummaryDto`, `FulfillmentReportResultDto`, `FulfillmentReportQuery`, `FulfillmentReportParams` (if defined here vs. only on the frontend — check), and `IReportsService.GetFulfillmentAsync`.

In `ReportsService.cs`, delete `GetFulfillmentAsync` (the ~275-line method building per-allocation rows).

In `ReportsController.cs`, delete the `GET /api/reports/fulfillment` route.

This report existed specifically to show "one row per sale-item-per-method-status allocation" — a concept that no longer exists once every sale has at most one schedule covering everything. The spec's "Reports" section asks only for a Delivery report, a Pickup report, and a simplified Sale-level status — nothing that needs per-allocation granularity.

- [ ] **Step 2: Update `GetDeliveryFulfillmentAsync`'s status derivation**

This method (the sale-level "Sale fulfillment" view under the Deliveries tab) already calls `SaleFulfillmentCalculator.Derive(...)` with the OLD 9-argument quantity signature. Read its current call site and replace it with the new 2-argument signature: since this view is delivery-only by design (its own doc comment says so), pass `method: FulfillmentMethod.Delivery` when a Pending/Completed delivery total exists for the sale, else `null`, and the corresponding `FulfillmentStatus`. Concretely: if the sale's `TotalDelivered > 0` treat it as `(Delivery, Completed)`; else if `TotalPending > 0` treat it as `(Delivery, Pending)`; else `(null, null)` → `TakeNow`. Adjust the surrounding query/projection only as much as needed to supply those two values — do not otherwise restructure this method.

- [ ] **Step 3: Verify Delivery/Pickup report shapes against the spec**

Re-read the spec's "Delivery report" and "Pickup report" sections and diff them field-by-field against `DeliveryReportRowDto`/`PickupReportRowDto` (`ReportsContracts.cs`). Confirm every spec-named field is present (Sale #, sequence, scheduled/expected date, status, recipient/customer, address (delivery only), contact, notes, delivery charge (delivery only), delivered/claimed timestamps, cancelled timestamp, cancellation reason + disposition). These two reports were already built to this shape by the prior plan and should need no structural change — if a field is genuinely missing, add it; do not restructure what already matches.

- [ ] **Step 4: Update tests**

Delete `FulfillmentReportTests.cs`'s tests that exercise the deleted `GetFulfillmentAsync`/combined view (the file may become empty and deletable, or may keep a handful of tests if it also covered something else — check before deleting the whole file). Update any `ReportsTests.cs` assertions using the old `SaleFulfillmentStatus` values.

- [ ] **Step 5: Run and verify**

Run: `dotnet build` — expect zero errors solution-wide (this is the point where Task 3's deferred `ReportsService.cs` break gets fixed). Run: `dotnet test tests/Negosio.IntegrationTests --filter "FullyQualifiedName~Reports"`.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(reports): drop the per-allocation combined report, update sale-level status derivation"
```

---

### Task 5: Backend test suite closure

**Files:** none unless a gap is found.

**Interfaces:**
- Consumes: everything from Tasks 1-4.
- Produces: a fully green `dotnet test` run — every later task assumes this baseline.

- [ ] **Step 1: Full solution build and test run**

Run: `dotnet build` from the repo root. Expected: 0 errors. Fix anything Tasks 1-4 missed (there will likely be stray references in files not explicitly listed above — e.g. a seed/demo script under `src/` that constructs the old contract shapes).

Run: `dotnet test` (both `Negosio.UnitTests` and `Negosio.IntegrationTests`). Expected: 100% pass.

- [ ] **Step 2: Coverage check against the spec's 15 backend acceptance criteria**

Re-read `docs/superpowers/specs/2026-09-19-simplify-fulfillment.md`'s "Backend acceptance criteria" list (15 items). For each, name the actual test method (file + name) that covers it — most were added in Tasks 1-2; a few (delivered/claimed completion, cancelled-remains-historical, tenant isolation, authorization) are already covered by the PRIOR plan's still-valid tests (nothing in this plan touches `MarkDeliveredAsync`/`MarkClaimedAsync`/`CancelWithDispositionAsync`/`GuardBranchAsync`/authorization policies). If any of the 15 has no covering test, add one now, in the file most relevant to what it tests.

- [ ] **Step 3: Commit (only if Step 1 or 2 required changes)**

```bash
git add -A
git commit -m "test: close backend coverage gaps for the whole-sale fulfillment simplification"
```

---

### Task 6: Frontend — `types.ts` and API clients

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/api/fulfillment.ts`
- Modify: `web/negosio-web/src/api/reports.ts`
- Modify: wherever `checkoutApi`/`CheckoutRequest`/`CheckoutItemInput` are defined (grep — likely `api/pos.ts` or similar)

**Interfaces:**
- Consumes: Tasks 1-4's backend contract shapes exactly (read the actual C# records, do not guess field casing — camelCase over the wire as established throughout this codebase).
- Produces: every later frontend task's types.

- [ ] **Step 1: Mirror the checkout contract**

`CheckoutItemInput`: remove `deliveryRequiredQuantity`/`pickupRequiredQuantity`. `CheckoutRequest`: add `method: FulfillmentMethod` (the existing `FulfillmentMethod` type from the 2026-09-13 plan already covers `'TakeNow' | 'Delivery' | 'Pickup'` — reuse it, do not redefine).

- [ ] **Step 2: Mirror the schedule-creation contract**

`CreateDeliveryReceiptRequest`/`CreatePickupRequest`: remove `items`. Delete `CreateDeliveryReceiptBatchRequest`, `CreatePickupBatchRequest`, `FulfillmentBatchResultDto`, `FulfillmentItemInput` — none of these are referenced anywhere after this plan (Task 8 builds schedule-creation requests with no item list at all, since the backend now resolves items itself).

In `api/fulfillment.ts`, delete `createDeliveryBatch`/`createPickupBatch`. `createDelivery`/`createPickup` keep their existing routes (`POST /api/sales/{saleId}/delivery-receipts`, `POST /api/sales/{saleId}/pickups`) with the now-`items`-free request body.

**Also mirror Task 3B's cancel-request additions** (Task 3B lands before this task and changes the backend contract — read its section above for the full rationale): `CancelDeliveryRequest` gains `rescheduledDelivery: DeliveryReplacementInput | null` (required when `disposition === 'DeliverLater'`, `null` otherwise — a *Delivery*-shaped replacement, since `DeliverLater`'s existing `replacement` field is Pickup-shaped and cannot represent this). `CancelPickupRequest` gains `rescheduledPickup: PickupReplacementInput | null` (required when `disposition === 'PickupLater'`, `null` otherwise). Both new fields are optional/nullable, matching the backend's C# `= null` defaults. `DeliveryReplacementInput`/`PickupReplacementInput` already exist as types here — reuse them, do not redefine.

- [ ] **Step 3: Mirror the sale-fulfillment-summary contract**

`SaleFulfillmentStatus`: replace the 8-value union with the 6 spec-named values (match the backend enum's C# names translated to the existing camelCase convention — check how the 2026-09-13 plan named other enums over the wire, e.g. `FulfillmentStatus`'s `'Unscheduled' | 'Pending' | 'Completed' | 'Cancelled'`, and use the same PascalCase-as-string convention: `'TakeNow' | 'PendingDelivery' | 'Delivered' | 'PendingPickup' | 'Claimed' | 'CancelledOrReplaced'`).

`SaleFulfillmentSummaryDto`: remove `items`/`canCreateDelivery`/`canCreatePickup`; add `activeSchedule: FulfillmentScheduleDto | null`; keep `deliveryCharge`, `deliveries`, `pickups`, `conversions`. Delete `SaleItemFulfillmentDto`.

- [ ] **Step 4: Delete the combined-report types**

Delete `FulfillmentReportRowDto`, `FulfillmentReportSummaryDto`, `FulfillmentReportResultDto`, `FulfillmentReportParams` from `types.ts`; delete `reportsApi.fulfillment` from `api/reports.ts`.

- [ ] **Step 5: Run `tsc -b`, expect new errors — do not fix them here**

Run: `cd web/negosio-web && npx tsc -b`. Expect many errors in the component files this contract change breaks (`PaymentModal.tsx`, `FulfillmentAllocationFields.tsx`, `PosTerminal.tsx`, `SaleDetailPage.tsx`, `FulfillmentReportsPage.tsx`, etc.) — that is expected and correct; those are Tasks 7-11's job. Just confirm the errors are confined to component files consuming these types, not to `types.ts`/`fulfillment.ts`/`reports.ts` themselves (those three files should compile clean on their own — a stray syntax error in them is this task's bug, not a downstream one).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): mirror the whole-sale fulfillment contracts in types.ts and API clients"
```

---

### Task 7: Frontend — remove per-line allocation state from the cart

**Files:**
- Modify: `web/negosio-web/src/hooks/usePosCart.ts`
- Modify: `web/negosio-web/src/lib/posStorage.ts`
- Modify: `web/negosio-web/src/components/pos/CartPanel.tsx`

**Interfaces:**
- Consumes: nothing new.
- Produces: `CartLine` with no `deliveryRequiredQuantity`/`pickupRequiredQuantity`; `PosCart` with no `setDeliveryRequired`/`setPickupRequired`. Task 8 relies on these being gone (it introduces the replacement sale-level state).

Note: `CartItem.tsx` and `CartPanel.tsx` were **already cleaned of allocation UI** in a prior fix on this branch (commit `e1f426a`, "move fulfillment allocation from cart lines into the Payment modal") — this task removes the underlying *state*, which that fix deliberately left in place because at the time it still fed the Payment modal's allocation table. That allocation table is being deleted in Task 9; this task removes what fed it.

- [ ] **Step 1: `posStorage.ts`**

Remove `deliveryRequiredQuantity`/`pickupRequiredQuantity` from the `CartLine` interface.

- [ ] **Step 2: `usePosCart.ts`**

Remove the `setDeliveryRequired`/`setPickupRequired` actions from the reducer's `Action` union and `switch`, remove them from the `PosCart` interface and the hook's returned object. In the `add` reducer case, remove the two fields from the new-line object. In the `setQty` reducer case, remove the whole "claw back pickup then delivery on shrink" block (lines computing `overflow`/`pickupCut`/`stillOver`) — quantity can shrink freely now, there is nothing to reconcile against.

- [ ] **Step 3: `CartPanel.tsx`**

Confirm (it should already be true post-`e1f426a`) that `onSetDeliveryRequired`/`onSetPickupRequired` are not in its `Props` — if `PosTerminal.tsx` still passes them as unused props at this point in the plan, that is fine, Task 8 removes that.

- [ ] **Step 4: Run `tsc -b`**

Run: `cd web/negosio-web && npx tsc -b`. Expect errors in `PosTerminal.tsx` (still calling the now-deleted `cart.setDeliveryRequired`/`setPickupRequired` and still passing `onSetDeliveryRequired`/`onSetPickupRequired` to `PaymentModal`) — expected, Task 8 fixes it.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(web): remove per-line delivery/pickup allocation state from the cart"
```

---

### Task 8: Frontend — sale-level fulfillment state and simplified checkout/schedule submission in `PosTerminal.tsx`

**Files:**
- Modify: `web/negosio-web/src/components/pos/PosTerminal.tsx`
- Modify: `web/negosio-web/src/lib/pos.ts`

**Interfaces:**
- Consumes: Task 6's `FulfillmentMethod`, `CheckoutRequest.method`, `CreateDeliveryReceiptRequest`/`CreatePickupRequest` (no `items`); Task 7's cart with no allocation state.
- Produces: `fulfillmentMethod`, `deliveryFields`, `pickupFields`, `deliveryCharge` state and their setters — Task 9's `PaymentModal` rewrite consumes these as props exactly as named here.

This is the task most likely to need judgment calls — read the current file's relevant sections (`useFulfillmentSchedules` at the top, the `delivery`/`pickup` state declarations, the reconciliation `useEffect`, both batch mutations, and the checkout `mutation`'s `onSuccess`) in full before changing anything.

- [ ] **Step 1: Replace the multi-schedule state in `lib/pos.ts`**

Delete `FulfillmentSchedule`, `FulfillmentScheduleItemAllocation`, `emptyFulfillmentSchedule`, `scheduledQuantityFor`, `reconcileSchedules`, `computeFulfillmentAllocationSummary` (the last one only existed for the just-deleted `FulfillmentAllocationFields` table — Task 9 deletes that component). Add one flat type, used for both methods (Delivery's extra `deliveryAddress` field is simply unused/empty for Pickup — mirrors how `FulfillmentSchedule` already worked before this plan):

```typescript
export interface FulfillmentDetails {
  scheduledDate: string // yyyy-MM-dd
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  notes: string
}

export function emptyFulfillmentDetails(): FulfillmentDetails {
  return {
    scheduledDate: todayLocalDateInput(),
    recipientName: '',
    deliveryAddress: '',
    contactNumber: '',
    notes: '',
  }
}
```

Keep `todayLocalDateInput`, `FULFILLMENT_METHOD_LABELS`, `CANCELLATION_DISPOSITION_LABELS`, `fulfillmentStatusLabel`, `fulfillmentStatusTone` — those are unaffected (they describe individual schedule records, not the multi-schedule builder).

- [ ] **Step 2: Replace `PosTerminal.tsx`'s fulfillment state**

Delete the `useFulfillmentSchedules()` function and its two instantiations (`delivery`/`pickup`). Delete `deliveryBatchIdRef`/`pickupBatchIdRef`/`ensureDeliveryBatchId`/`ensurePickupBatchId` (there is only ever one schedule submission now — a single `clientRequestId`-style ref is enough if you want retry-dedup on the frontend side at all, but per Task 2's design note, the backend's one-active-schedule guard already makes a naive retry safe without one; you may drop the ref entirely). Delete the whole cart-reconciliation `useEffect` (lines ~331-350 as read) — nothing to reconcile once schedules aren't built from live per-line allocation.

Replace with:

```typescript
const [fulfillmentMethod, setFulfillmentMethod] = useState<FulfillmentMethod>('TakeNow')
const [deliveryFields, setDeliveryFields] = useState<FulfillmentDetails>(emptyFulfillmentDetails())
const [pickupFields, setPickupFields] = useState<FulfillmentDetails>(emptyFulfillmentDetails())
// deliveryCharge state (EMPTY_DELIVERY_CHARGE) is unchanged — keep it exactly as it is today.
```

`resetFulfillmentState`: reset `fulfillmentMethod` to `'TakeNow'`, both field states to `emptyFulfillmentDetails()`, and `deliveryCharge` to `EMPTY_DELIVERY_CHARGE` — same call sites as today (after a successful checkout, on New Transaction, on explicit reset). Per the spec's "Payment and retry" section, do **not** reset on a failed checkout — this already matches the existing pattern (`resetFulfillmentState` is only called in the checkout mutation's `onSuccess`, never `onError`).

- [ ] **Step 3: Simplify the checkout mutation**

In the `mutation`'s `mutationFn`, replace the `items` mapping (drop the two per-item quantity fields) and add `method: fulfillmentMethod` to the `CheckoutRequest` body:

```typescript
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
  method: fulfillmentMethod,
  deliveryCharge: fulfillmentMethod === 'Delivery' ? roundMoney(parseDeliveryCharge(true, deliveryCharge)) : 0,
  approval,
}
```

In `onSuccess`, replace the two batch-mutation dispatches with at most one single-create call. Neither mutation needs any item data — `CreateDeliveryReceiptRequest`/`CreatePickupRequest` no longer take an `items` field at all (Task 6), since the backend resolves "every item on the sale" itself (Task 2). Only `saleId`/`saleNumber` are needed, the latter purely for the error toast:

```typescript
if (fulfillmentMethod === 'Delivery') {
  createDeliveryMutation.mutate({ saleId: result.saleId, saleNumber: result.saleNumber })
} else if (fulfillmentMethod === 'Pickup') {
  createPickupMutation.mutate({ saleId: result.saleId, saleNumber: result.saleNumber })
} else {
  setSuccessDeliveryCount(0)
  setSuccessPickupCount(0)
}
```

- [ ] **Step 4: Replace the two batch mutations with two single-create mutations**

Delete `createDeliveryBatchMutation`/`createPickupBatchMutation` in full (including their stale-item reconciliation `.map/.filter/.flatMap` and the `resultItems`/`FulfillmentItemInput` parameters they took — none of that is needed once a schedule request carries no item list at all). Replace with:

```typescript
const createDeliveryMutation = useMutation({
  mutationFn: ({ saleId }: { saleId: string; saleNumber: string }) =>
    fulfillmentApi.createDelivery(saleId, {
      scheduledDate: deliveryFields.scheduledDate,
      recipientName: deliveryFields.recipientName.trim(),
      deliveryAddress: deliveryFields.deliveryAddress.trim(),
      contactNumber: deliveryFields.contactNumber.trim() || null,
      notes: deliveryFields.notes.trim() || null,
    }),
  onSuccess: () => setSuccessDeliveryCount(1),
  onError: (_err, variables) => {
    toast(
      'error',
      `Sale #${variables.saleNumber} completed, but the delivery could not be scheduled. Schedule it from the sale's detail page.`,
    )
  },
})
```

Mirror for `createPickupMutation` (no `deliveryAddress`).

- [ ] **Step 5: Update the `<PaymentModal>` render call**

Remove the `delivery`/`pickup` schedule-array props and `onSetDeliveryRequired`/`onSetPickupRequired`. Pass the new flat props instead — the exact prop names Task 9 expects: `fulfillmentMethod`, `onFulfillmentMethodChange`, `deliveryFields`, `onDeliveryFieldsChange`, `pickupFields`, `onPickupFieldsChange`, `deliveryCharge`, `onDeliveryChargeChange` (unchanged).

- [ ] **Step 6: Run `tsc -b`**

Run: `npx tsc -b`. Expect remaining errors confined to `PaymentModal.tsx`/`FulfillmentAllocationFields.tsx`/`FulfillmentDetailsFields.tsx` — Task 9's job.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(web): sale-level fulfillment state and single-schedule checkout submission in PosTerminal"
```

---

### Task 9: Frontend — Payment modal rewrite (three-way selector, flat schedule fields)

**Files:**
- Modify: `web/negosio-web/src/components/pos/PaymentModal.tsx`
- Modify: `web/negosio-web/src/components/pos/FulfillmentDetailsFields.tsx` (rewrite to a flat single form)
- Delete: `web/negosio-web/src/components/pos/FulfillmentAllocationFields.tsx`

**Interfaces:**
- Consumes: Task 8's `fulfillmentMethod`/`onFulfillmentMethodChange`/`deliveryFields`/`onDeliveryFieldsChange`/`pickupFields`/`onPickupFieldsChange` props.
- Produces: the UI the spec's 20 UI acceptance criteria verify directly.

- [ ] **Step 1: Delete `FulfillmentAllocationFields.tsx`**

It has no reason to exist once there is nothing to allocate per line.

- [ ] **Step 2: Rewrite `FulfillmentDetailsFields.tsx` to a flat, single-schedule form**

Drop the `schedules: FulfillmentSchedule[]` array, the per-schedule card wrapper, "+ Add another delivery/pickup", `onAddSchedule`/`onRemoveSchedule`, the per-item `QuantityInput` row-list (there is no item picker anymore — every item is included automatically, and the spec explicitly says "Do not ask the cashier to select products or quantities"). Keep the flat fields exactly as they already render per schedule today (date/recipient/address-for-delivery/contact/notes) but as ONE set, not N:

```typescript
interface Props {
  method: FulfillmentMethod // never 'TakeNow'
  values: FulfillmentDetails
  onChange: (patch: Partial<FulfillmentDetails>) => void
  deliveryCharge?: string // Delivery only
  onDeliveryChargeChange?: (value: string) => void
  errors: { recipientName?: string; deliveryAddress?: string; scheduledDate?: string; deliveryCharge?: string }
  attempted: boolean
  disabled?: boolean
}
```

Render: delivery charge field (Delivery only, unchanged from today), scheduled date (`min={todayLocalDateInput()}`, unchanged validation), recipient name, delivery address (Delivery only, textarea, unchanged), contact number, notes — in a 2-column grid for contact/notes as today. No "schedules" heading, no per-item table, no add/remove button.

- [ ] **Step 3: Rewrite the fulfillment section of `PaymentModal.tsx`**

Replace the current "Fulfillment" allocation-table box with a compact 3-way segmented control, then conditionally render exactly one `FulfillmentDetailsFields` instance:

```tsx
<div className="mb-4 rounded-lg border border-border-strong px-4 py-3.5">
  <p className="text-sm font-semibold text-text-secondary">How will the customer receive the order?</p>
  <div className="mt-3 grid grid-cols-3 gap-2">
    {(['TakeNow', 'Delivery', 'Pickup'] as const).map((m) => (
      <button
        key={m}
        type="button"
        aria-pressed={fulfillmentMethod === m}
        onClick={() => onFulfillmentMethodChange(m)}
        className={cn(
          'rounded-lg border px-3 py-2 text-[13px] font-semibold transition-all',
          fulfillmentMethod === m
            ? 'border-primary-500 bg-primary-50 text-primary-700 shadow-sm'
            : 'border-border-strong text-text-secondary hover:border-primary-200 hover:bg-surface-subtle',
        )}
      >
        {m === 'TakeNow' ? 'Take now' : m === 'Delivery' ? 'For delivery' : 'For pickup'}
      </button>
    ))}
  </div>

  {fulfillmentMethod === 'Delivery' && (
    <FulfillmentDetailsFields
      method="Delivery"
      values={deliveryFields}
      onChange={onDeliveryFieldsChange}
      deliveryCharge={deliveryCharge}
      onDeliveryChargeChange={onDeliveryChargeChange}
      errors={deliveryErrors}
      attempted={fulfillmentAttempted}
      disabled={submitting}
    />
  )}
  {fulfillmentMethod === 'Pickup' && (
    <FulfillmentDetailsFields
      method="Pickup"
      values={pickupFields}
      onChange={onPickupFieldsChange}
      errors={pickupErrors}
      attempted={fulfillmentAttempted}
      disabled={submitting}
    />
  )}
</div>
```

Use the existing design tokens/spacing already established in this file (`rounded-lg border border-border-strong px-4 py-3.5` is the box style already used for the Delivery/Pickup sections today) — do not introduce new ones.

- [ ] **Step 4: Update `PaymentModal`'s `Props` and derived state**

Replace `cartLines`/`delivery`/`pickup` props with: `fulfillmentMethod: FulfillmentMethod`, `onFulfillmentMethodChange: (m: FulfillmentMethod) => void`, `deliveryFields: FulfillmentDetails`, `onDeliveryFieldsChange: (patch: Partial<FulfillmentDetails>) => void`, `pickupFields`/`onPickupFieldsChange` (same shape). Drop `cartLines` entirely — the modal no longer needs to know per-line quantities at all.

Replace the derived `anyDelivery`/`anyPickup`/`deliveryLines`/`pickupLines`/`activeDeliverySchedules`/`deliveryComplete`/`activePickupSchedules`/`pickupComplete`/`fulfillmentComplete` with direct checks against `fulfillmentMethod`:

```typescript
const deliveryChargeValid = fulfillmentMethod !== 'Delivery' || isValidDeliveryChargeInput(deliveryCharge)
const effectiveAmountDue = amountDue + parseDeliveryCharge(fulfillmentMethod === 'Delivery', deliveryCharge)
const fulfillmentComplete =
  fulfillmentMethod === 'TakeNow' ||
  (fulfillmentMethod === 'Delivery'
    ? deliveryChargeValid && !!deliveryFields.scheduledDate && !!deliveryFields.recipientName.trim() && !!deliveryFields.deliveryAddress.trim()
    : !!pickupFields.scheduledDate && !!pickupFields.recipientName.trim())
```

- [ ] **Step 5: Handle the switching rules explicitly**

Per the spec's "Switching selections" section, when `onFulfillmentMethodChange` fires:
- Do **not** clear `deliveryFields`/`pickupFields` on switch — the spec says "It is acceptable to preserve draft values locally while the Payment modal remains open, but only the currently selected method may be submitted." Since `confirm()`'s branch on `fulfillmentMethod` already only ever reads the fields for the currently-selected method (Step 6 below), stale draft values in the *other* method's fields are simply never sent — no explicit clearing logic is needed. Do not add any.
- Switching to `Delivery` from `Pickup` (or vice versa) needs no special-case code beyond the conditional rendering above — this is a natural consequence of it.
- `deliveryCharge` normalizes to effectively-zero for a non-Delivery method purely because `effectiveAmountDue`'s `parseDeliveryCharge(fulfillmentMethod === 'Delivery', deliveryCharge)` call already gates on the method (mirrors the existing `parseDeliveryCharge(anyDelivery, deliveryCharge)` pattern this codebase already used before this plan) — the raw `deliveryCharge` string can stay in state untouched; only its *effect* is gated.

- [ ] **Step 6: Update `confirm()`**

```typescript
const confirm = () => {
  if (submitting || !paymentComplete) return
  if (!fulfillmentComplete) {
    setFulfillmentAttempted(true)
    return
  }
  if (method === 'Cash') {
    onConfirm({ method: 'Cash', receivedAmount: receivedNum })
  } else {
    onConfirm({ method, amount: effectiveAmountDue, referenceNumber: reference.trim() || null })
  }
}
```

(Unchanged in shape — `onConfirm`'s own payload was never about fulfillment; `PosTerminal`'s mutation already reads `fulfillmentMethod` from its own state, not from this callback's argument.)

- [ ] **Step 7: Run `tsc -b` and `npm run build`**

Run: `cd web/negosio-web && npx tsc -b && npm run build`. Expected: zero errors from both — this is the point where every fulfillment-related frontend file except Sale Detail and Reports should compile clean (those are Tasks 10-11).

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(web): three-way Take now/Delivery/Pickup selector replaces the allocation table in the Payment modal"
```

---

### Task 10: Frontend — Sale Detail simplification

**Files:**
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`
- Modify: `web/negosio-web/src/components/sales/CancelFulfillmentModal.tsx` (verify only — expect no or trivial changes)
- Delete: `web/negosio-web/src/components/sales/FulfillmentBreakdownTable.tsx`
- Delete: `web/negosio-web/src/components/sales/CreateFulfillmentScheduleModal.tsx`

**Interfaces:**
- Consumes: Task 3/6's `SaleFulfillmentSummaryDto` (`activeSchedule`, `deliveries`, `pickups`, `conversions`, `fulfillmentStatus`).
- Produces: the updated `SALE_FULFILLMENT_STATUS_LABELS`/`saleFulfillmentStatusTone` in `lib/pos.ts` (this task owns that update, not Task 11 — Sale Detail needs the new 6-value map to compile against `fulfillmentStatus`'s new type before Task 11 ever runs; Task 11 only verifies it, it does not redo it).
- Produces: the simplified Sale Detail surface the spec's "Sale details and receipts" section describes.

- [ ] **Step 1: Delete the two dead components**

`FulfillmentBreakdownTable.tsx` rendered the 8-row per-item breakdown from the now-deleted `SaleItemFulfillmentDto[]` — delete it. `CreateFulfillmentScheduleModal.tsx` existed to create an *additional* schedule for remaining unscheduled quantity — under the new model there is no such action (the only ways a sale gets a schedule are checkout, or cancel+convert's own modal) — delete it and its "Create delivery"/"Create pickup" trigger buttons in `SaleDetailPage.tsx`.

- [ ] **Step 2: Update `SALE_FULFILLMENT_STATUS_LABELS` in `lib/pos.ts`**

This page needs the new 6-value map to compile against `summary.fulfillmentStatus`'s new type (Task 6), so this task owns the update rather than deferring it to Task 11. Replace the 8-entry map with:

```typescript
export const SALE_FULFILLMENT_STATUS_LABELS: Record<SaleFulfillmentStatus, string> = {
  TakeNow: 'Take now',
  PendingDelivery: 'Pending delivery',
  Delivered: 'Delivered',
  PendingPickup: 'Pending pickup',
  Claimed: 'Claimed',
  CancelledOrReplaced: 'Cancelled / replaced',
}
```

Update `saleFulfillmentStatusTone` similarly: `Delivered`/`Claimed` → `'success'`; `PendingDelivery`/`PendingPickup` → `'warning'`; `CancelledOrReplaced` → `'danger'`; `TakeNow` → `'neutral'`.

- [ ] **Step 3: Simplify `SaleDetailPage.tsx`'s fulfillment section**

Replace the breakdown table + two schedule-list sections + create buttons with:
- A single status line using `SALE_FULFILLMENT_STATUS_LABELS[summary.fulfillmentStatus]` (Take now Sale: per spec, "Show fulfillment method as Take now if useful" — a simple label is enough, no special-casing needed beyond the label map already covering `'TakeNow'`).
- When `summary.deliveries.length > 0 || summary.pickups.length > 0`: render one section (title it by whichever method the sale actually used — read `summary.activeSchedule?.method` or, if null (fully cancelled with a replacement being shown instead — should not normally happen per Task 3's note), fall back to whichever of `deliveries`/`pickups` is non-empty) listing every schedule in that method's history (active one first, then cancelled ones), each showing: sequence number, status badge (`FulfillmentStatusBadge`, unchanged), scheduled/expected date, recipient/customer, address (delivery only), contact, **notes rendered prominently** (the spec repeatedly calls this out — do not bury it in a collapsed/secondary spot; render it as its own labelled block, not a truncated aside), delivery charge (delivery only, read from `summary.deliveryCharge`, shown once), and for a cancelled row: `cancellationReason` + `CANCELLATION_DISPOSITION_LABELS[cancellationDisposition]`.
- The complete Sale item list: this is now the Sale's own existing line-items table (already rendered elsewhere on this page for every sale, take-now included) — the spec's "Complete Sale item list" requirement under Delivery/Pickup Sale is satisfied by that existing table, not a duplicate. Do not build a second item list.
- Mark-Delivered/Mark-Claimed/Cancel buttons: gate on `summary.activeSchedule` (present and `Pending`) instead of iterating a list — there is only ever one active schedule to act on.
- Conversion history (`ConversionHistoryList`, unchanged component) continues to render from `summary.conversions` exactly as today.

- [ ] **Step 4: Update `CancelFulfillmentModal.tsx` for Task 3B's `DeliverLater`/`PickupLater` fix**

**Correction:** this step originally said the modal needed no changes. That was wrong — Task 3B
(inserted mid-plan, see its own section above) changed the backend so `DeliverLater`/`PickupLater`
now *require* a replacement payload (a new same-method schedule) instead of forbidding one. The
modal must be updated to match, in `web/negosio-web/src/components/sales/CancelFulfillmentModal.tsx`:

- **`needsReplacement(disposition)`** — every disposition now creates a replacement (there is no
  more "pure release" option). Delete this function; the `showReplacementForm` variable and its
  one call site (`const showReplacementForm = needsReplacement(disposition)`) can simply become
  `const showReplacementForm = true` — or, cleaner, delete `showReplacementForm` entirely and
  render the sub-form unconditionally, removing every `{showReplacementForm && (...)}`/`if
  (showReplacementForm)` guard around it.
- **`replacementMethod(disposition)`** — currently `disposition === 'ConvertToDelivery' ? 'Delivery' : 'Pickup'`,
  which is now wrong for `DeliverLater` (a Delivery-cancellation disposition whose replacement is
  *also* a Delivery, not a Pickup). Fix to:
  ```typescript
  function replacementMethod(disposition: CancellationDisposition): 'Delivery' | 'Pickup' {
    return disposition === 'DeliverLater' || disposition === 'ConvertToDelivery' ? 'Delivery' : 'Pickup'
  }
  ```
- **`requiresFutureDate(disposition)`** — currently only `ConvertToPickup`/`ConvertToDelivery`. Per
  the spec, `DeliverLater`/`PickupLater` also "require a new valid delivery/pickup date" (today or
  future) — only `CustomerPickedUpInstead` is exempt (it records something that already happened).
  Fix to: `return disposition !== 'CustomerPickedUpInstead'`.
- **`pastDateMessage(disposition)`** — currently a two-way ternary covering only
  `ConvertToPickup`/`ConvertToDelivery`. Since `replacementMethod(disposition)` now correctly
  covers all 4 date-gated dispositions, simplify to one line using it directly:
  `` `The scheduled ${replacementMethod(disposition).toLowerCase()} date cannot be in the past.` ``
  — deleting the manual ternary rather than extending it.
- **The `summary` `useMemo`'s `DeliverLater`/`PickupLater` cases** — currently: `` `${scheduleLabel}
  will be cancelled. ${itemsSummary(schedule.items)} returns to unscheduled.` ``. This is no longer
  true (nothing "returns to unscheduled" — a new schedule is created immediately). Replace both
  cases with the same shape the `ConvertToPickup`/`ConvertToDelivery` cases already use:
  `` `${scheduleLabel} will be cancelled and a new ${replacementMethod(disposition)} created for ${formatScheduleDate(scheduledDate)}.` ``
  (`replacementMethod('DeliverLater')` → `'Delivery'`, `replacementMethod('PickupLater')` →
  `'Pickup'` — reads correctly either way with no special-casing needed).
- **The mutation's request-body construction** — currently sends the Pickup-shaped `replacement`
  field (for Delivery cancellations) or the Delivery-shaped `replacement` field (for Pickup
  cancellations) whenever `showReplacementForm` is true, `null` otherwise. Now that every
  disposition shows the form, but `DeliverLater`/`PickupLater` need to populate the NEW contract
  fields (Task 3B's `RescheduledDelivery`/`RescheduledPickup`) instead of the existing
  `Replacement` field, branch on disposition explicitly:
  ```typescript
  if (isDelivery) {
    const replacementFields = {
      scheduledDate, recipientName: recipientName.trim(),
      contactNumber: contactNumber.trim() || null, notes: notes.trim() || null,
    }
    const body: CancelDeliveryRequest = {
      reason: trimmedReason,
      disposition,
      replacement: disposition === 'ConvertToPickup' || disposition === 'CustomerPickedUpInstead' ? replacementFields : null,
      rescheduledDelivery: disposition === 'DeliverLater'
        ? { ...replacementFields, deliveryAddress: deliveryAddress.trim() }
        : null,
    }
    return fulfillmentApi.cancelDelivery(schedule.id, body)
  }
  // mirror for the Pickup branch: `replacement` (Delivery-shaped, deliveryAddress required) only
  // for ConvertToDelivery; new `rescheduledPickup` (Pickup-shaped) only for PickupLater.
  ```
  Match the exact field names Task 6 gives `CancelDeliveryRequest`/`CancelPickupRequest` in
  `types.ts` (`rescheduledDelivery`/`rescheduledPickup`, camelCase over the wire per this
  codebase's established convention) — read that file rather than guessing the casing.
- **Validation in `submit()`** — the `if (showReplacementForm)` block's field-requiredness checks
  (date/recipient/address) already apply correctly to all 5 dispositions once
  `showReplacementForm` is always true; the one disposition-specific check,
  `if (disposition === 'ConvertToDelivery' && !deliveryAddress.trim())`, needs a sibling for
  `DeliverLater` (also Delivery-shaped, also needs an address): change to
  `if ((disposition === 'ConvertToDelivery' || disposition === 'DeliverLater') && !deliveryAddress.trim())`.
- **`onError`'s field-error mapping** — currently checks `fields['replacement.scheduleddate']`
  etc. Confirm (read `src/Negosio.Application/Common/ValidationExtensions.cs`'s camelCasing, or
  just test live) whether a validation error on the new `RescheduledDelivery`/`RescheduledPickup`
  field comes back as `rescheduleddelivery.scheduleddate` etc., and add the matching lookups
  alongside the existing `replacement.*` ones if so — do not assume without checking, since a
  missed mapping here means a real backend validation error would silently show nothing.

Read the whole file once before starting — this touches most of its logic, not an isolated corner.

- [ ] **Step 5: Run `tsc -b`**

Run: `npx tsc -b`. Expect remaining errors confined to `FulfillmentReportsPage.tsx` — Task 11.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): simplify Sale Detail to a single active schedule, drop the per-item breakdown and create-schedule modal"
```

---

### Task 11: Frontend — Reports simplification

**Files:**
- Modify: `web/negosio-web/src/pages/FulfillmentReportsPage.tsx`

**Interfaces:**
- Consumes: Task 4/6's simplified `DeliveryFulfillmentReportRowDto`/`SaleFulfillmentStatus`; the deletion of `FulfillmentReportResultDto`/`reportsApi.fulfillment`; Task 10's already-updated `SALE_FULFILLMENT_STATUS_LABELS`/`saleFulfillmentStatusTone` (do not redo that update here — it must already be in place for this task's own `tsc -b` to have any hope of passing, since Task 10 runs first).

- [ ] **Step 1: Delete the "All fulfillment" tab**

Remove the third tab (`'all'`) entirely — its `allQuery`/`FulfillmentReportRowDto` table/`statusOptionLabel`/`historyLabel`/method-filter dropdown/11-tile summary all depended on the now-deleted combined endpoint. `Tab` narrows to `'deliveries' | 'pickups'`.

- [ ] **Step 2: Confirm the status labels are already updated**

`SALE_FULFILLMENT_STATUS_LABELS`/`saleFulfillmentStatusTone` in `lib/pos.ts` should already carry the 6 new values — Task 10 owns that update. Read them to confirm; if somehow still the old 8-value map (Task 10 skipped it), update it now using the exact same code shown in Task 10's Step 2 rather than inventing different label text.

- [ ] **Step 3: Update the Deliveries tab's "Sale fulfillment" sub-view status filter options**

It already lists every `SaleFulfillmentStatus` value in a dropdown — this now iterates 6 values instead of 8 with no other code change, since it already reads from `SALE_FULFILLMENT_STATUS_LABELS`.

- [ ] **Step 4: Verify Delivery/Pickup notes are shown prominently on both report tabs**

The spec explicitly lists "Relevant Delivery report/detail views" and "Pickup reports" among the places Delivery/Pickup notes must display prominently — check the Deliveries and Pickups tabs' table columns now (`DeliveryReportRowDto.Notes`/`PickupReportRowDto.Notes` already exist per Task 4 Step 3). If notes are missing from either table entirely, add a `Notes` column. If a column already exists but truncates long text silently with no way to read the full note (e.g. a fixed-width cell with `overflow-hidden` and no tooltip/expansion), that does not satisfy "prominently" — add a `title` attribute with the full text at minimum, or a wrap-on-hover affordance consistent with this codebase's existing patterns elsewhere (check `SaleDetailPage.tsx`'s Task 10 notes block for the pattern this task should match). Do not build a new component for this if a small class-name change on the existing cell is enough.

- [ ] **Step 5: Run `tsc -b` and `npm run build`**

Run: `cd web/negosio-web && npx tsc -b && npm run build`. Expected: **zero errors from both** — this is the plan's frontend closure checkpoint.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): drop the combined fulfillment report tab, simplify sale-level status labels"
```

---

### Task 12: Final verification and report

**Files:** none unless a defect is found — this task is verification-only, matching the prior plan's Task 19 pattern.

**Interfaces:**
- Consumes: everything from Tasks 1-11.

- [ ] **Step 1: Backend**

Run `dotnet test` from the repo root. Record the exact pass/fail count.

- [ ] **Step 2: Frontend**

Run `cd web/negosio-web && npx tsc -b && npm run build`. Expect zero errors from both.

- [ ] **Step 3: Live POS checkout — all three methods**

Using a seed script (extend the existing scratchpad `seed-test.mjs` if it still references the old per-item allocation shape, or drive it live via the browser), verify against a locally running API + frontend:
1. Take-now checkout: no Delivery/Pickup record created, no fulfillment fields shown, checkout stays fast.
2. Delivery checkout: one Pending Delivery created covering every sale item at full quantity; delivery charge stored once, included once in the total.
3. Pickup checkout: one Pending Pickup created covering every sale item at full quantity; no delivery charge.

- [ ] **Step 4: Live cancellation/replacement scenarios**

Cancel a Delivery with each of its three dispositions (DeliverLater, ConvertToPickup, CustomerPickedUpInstead) and a Pickup with each of its two (PickupLater, ConvertToDelivery) — confirm each produces exactly the record described in the spec's "Cancellation and replacement behavior" section, and that the replacement always covers the complete sale (it will, by construction, since Task 2 guarantees every schedule already does).

- [ ] **Step 5: Reports**

Open the Deliveries and Pickups report tabs, confirm they render correctly with no console errors, confirm the combined/"All fulfillment" tab is gone.

- [ ] **Step 6: Confirm nothing was merged or pushed**

Run `git status -sb` and `git log --oneline origin/master..HEAD 2>/dev/null | wc -l` (or the equivalent against local `master`). Confirm branch is unchanged, nothing pushed.

- [ ] **Step 7: Write the final report**

Follow the spec's "When finished, report" list exactly: current implementation found, simplification approach selected (whole-sale application-layer constraint, zero schema/migration changes — explain why, as the spec explicitly asks for this explanation before any destructive change, and none turned out to be needed), UI components changed, partial/mixed controls removed, backend behavior changed, database structures retained (all of them, with reasoning), migration impact (none), receipt and report changes, tests run and exact results, live scenarios verified, remaining limitations.

Do not merge or push.
