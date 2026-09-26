# Fulfillment Recovery Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the two known gaps in the whole-sale fulfillment model — no recovery path when a post-checkout schedule call fails, and a voided sale stranding its pending delivery/pickup schedule.

**Architecture:** Both fixes reuse existing machinery end to end (existing endpoints, existing locks, existing audit shapes) rather than introducing new subsystems. Fix 1 (recovery UI) is almost entirely frontend, plus one small additive backend DTO field. Fix 2 (void cascade) adds one new `CancellationDisposition` enum value and refactors the existing cancel-with-disposition transaction into a reusable, transaction-less core so both the public cancel endpoint and the new void-triggered cascade can share it correctly.

**Tech Stack:** .NET 9 / EF Core 9 / SQL Server (LocalDB) backend; React 19 / TypeScript strict / Vite / TanStack Query v5 frontend.

**Spec:** `docs/superpowers/specs/2026-09-25-reports-phase-c-and-fulfillment-fixes.md` (Part 1 — this plan implements Part 1 only; Reports Phase C is a separate plan, `docs/superpowers/plans/2026-09-25-reports-phase-c.md`).

## Global Constraints

- Branch: `feature/pos-for-delivery`. **Do not merge to `master`. Do not push any branch.**
- Do not weaken the whole-sale invariant: one fulfillment method per sale, at most one active (non-Cancelled) schedule per sale, ever.
- The new `SaleVoided` disposition must be reachable only from the void code path — never selectable by a cashier through the public `CancelDeliveryRequest`/`CancelPickupRequest` API (i.e., do not add it to `DeliveryReceiptValidators.cs`'s `ValidDispositions` arrays).
- `dotnet test` must stay green (baseline: 497/497) after every task; `npx tsc -b` and `npm run build` must stay zero-error.
- Every task ends with a commit.

---

### Task 1: Backend — add `PickupRequiredQuantity` to `SaleItemDto`

**Files:**
- Modify: `src/Negosio.Application/Sales/SaleContracts.cs:6-21`
- Modify: `src/Negosio.Application/Sales/SaleQueryService.cs:117-123`
- Test: `tests/Negosio.IntegrationTests/Sales/SaleQueryTests.cs`

**Interfaces:**
- Consumes: `SaleItem.PickupRequiredQuantity` (already exists on the domain entity, alongside the already-mirrored `DeliveryRequiredQuantity`).
- Produces: `SaleItemDto.PickupRequiredQuantity` — Task 4 (frontend recovery UI) needs this field on `GET /api/sales/{id}`'s response to detect "this sale needs a pickup schedule" without any further backend change.

The current `SaleItemDto` record only carries `DeliveryRequiredQuantity`, not `PickupRequiredQuantity` — confirmed by reading the record and its only construction site. This is a pre-existing, narrow gap (most likely missed when Pickup was added as a second method), not something Task 4 can work around client-side.

- [ ] **Step 1: Write the failing test**

Add to `tests/Negosio.IntegrationTests/Sales/SaleQueryTests.cs` (open the file first to match its existing setup/helper conventions — e.g. however it already creates a completed Pickup sale for other tests in this file; reuse that helper rather than duplicating checkout/seed boilerplate):

```csharp
[Fact]
public async Task GetAsync_ReturnsPickupRequiredQuantityOnItems()
{
    // Arrange: complete a checkout with Method = Pickup (reuse this file's existing checkout helper).
    var sale = await CheckoutPickupSaleAsync(quantity: 3m); // adjust to this file's actual helper name/signature

    // Act
    var detail = await _saleQueryService.GetAsync(sale.Id, CancellationToken.None);

    // Assert
    var item = Assert.Single(detail.Items);
    Assert.Equal(3m, item.PickupRequiredQuantity);
    Assert.Equal(0m, item.DeliveryRequiredQuantity);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SaleQueryTests.GetAsync_ReturnsPickupRequiredQuantityOnItems"`
Expected: compile error (`SaleItemDto` has no `PickupRequiredQuantity` member) or, if you stub the property access out first to check runtime behavior, a test failure showing `0m` where `3m` was expected. Either failure mode confirms the gap.

- [ ] **Step 3: Add the field**

In `src/Negosio.Application/Sales/SaleContracts.cs`, change the `SaleItemDto` record:

```csharp
public sealed record SaleItemDto(
    Guid Id,
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    string? Sku,
    string? Barcode,
    decimal UnitPrice,
    decimal Quantity,
    decimal GrossAmount,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal NetAmount,
    decimal? CostPriceSnapshot,
    decimal ReturnedQuantity,
    decimal DeliveryRequiredQuantity,
    decimal PickupRequiredQuantity);
```

In `src/Negosio.Application/Sales/SaleQueryService.cs`, update the construction site:

```csharp
        var items = sale.Items
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i => new SaleItemDto(
                i.Id, i.ProductVariantId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.SkuSnapshot, i.BarcodeSnapshot,
                i.UnitPrice, i.Quantity, i.GrossAmount, i.DiscountAmount, i.TaxAmount, i.NetAmount,
                canViewCost ? i.CostPriceSnapshot : null, i.ReturnedQuantity, i.DeliveryRequiredQuantity,
                i.PickupRequiredQuantity))
            .ToList();
```

Grep the whole `src/` tree for any other `new SaleItemDto(` construction site before assuming this is the only one — the investigation found exactly one, but verify it yourself since a second, missed call site would be a compile error you'd want to catch immediately rather than have a fresh implementer discover cold.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~SaleQueryTests.GetAsync_ReturnsPickupRequiredQuantityOnItems"`
Expected: PASS.

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test` from the repo root. Expected: 498/498 (497 baseline + 1 new test), 0 failures.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(sales): expose PickupRequiredQuantity on SaleItemDto, mirroring the existing DeliveryRequiredQuantity"
```

---

### Task 2: Backend — `SaleVoided` disposition + reusable cancel-with-disposition core

**Files:**
- Modify: `src/Negosio.Domain/Enums/CancellationDisposition.cs`
- Modify: `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs` (the `Cancel` method's allowed-pairing switch, around line 279-315)
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs` (add one method to `IDeliveryReceiptService`, around line 179-193)
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` (refactor `CancelWithDispositionAsync`, add `CancelWithDispositionCoreAsync` and `CancelActiveScheduleForVoidedSaleAsync`)
- Test: `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs` (domain-level `Cancel` test)
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs` or `PickupTests.cs` (new integration tests for the void-cascade method — this task tests the method directly, not through `VoidSaleService`, which Task 3 wires up and tests end-to-end)

**Interfaces:**
- Consumes: nothing new from other tasks in this plan.
- Produces: `IDeliveryReceiptService.CancelActiveScheduleForVoidedSaleAsync(Guid saleId, string voidReason, IDbContextTransaction transaction, CancellationToken ct = default) : Task` — Task 3 (`VoidSaleService`) calls this exact signature, inside its own already-open transaction, and does not expect a return value.

**Do not skip:** read `src/Negosio.Application/Delivery/DeliveryReceiptService.cs`'s current `CancelWithDispositionAsync` (lines ~436-593), `LoadFulfillableSaleAsync` (~597-613), `LockSaleItemsAsync` (~634-638), and `EnsureMethod` (~663-671) in full before editing — this task restructures the most delicate transactional method in this codebase, and every existing line of behavior for the 5 current dispositions must survive byte-for-byte identical.

- [ ] **Step 1: Add the new enum value**

In `src/Negosio.Domain/Enums/CancellationDisposition.cs`, add a 6th member with a doc comment explaining its restricted reachability:

```csharp
    /// <summary>System-only. Set when the sale itself was voided while this schedule was still Pending —
    /// the schedule is released with no replacement, since there is no sale left to fulfill. Unlike every
    /// other disposition, this one is never selectable through the public cancel endpoints
    /// (<c>CancelDeliveryRequest</c>/<c>CancelPickupRequest</c> validators deliberately exclude it from
    /// their allowed-dispositions lists) — the only caller that may ever pass this value is
    /// <c>VoidSaleService</c>, via <c>IDeliveryReceiptService.CancelActiveScheduleForVoidedSaleAsync</c>.</summary>
    SaleVoided = 6
```

- [ ] **Step 2: Update the domain-level allowed-pairing check**

In `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs`, the `Cancel` method's `allowed` switch currently rejects any disposition not in its per-method list — `SaleVoided` must be added to BOTH branches, since a voided sale's stranded schedule can be either method:

```csharp
        var allowed = Method switch
        {
            FulfillmentMethod.Delivery =>
                disposition is Enums.CancellationDisposition.DeliverLater
                    or Enums.CancellationDisposition.ConvertToPickup
                    or Enums.CancellationDisposition.CustomerPickedUpInstead
                    or Enums.CancellationDisposition.SaleVoided,
            FulfillmentMethod.Pickup =>
                disposition is Enums.CancellationDisposition.PickupLater
                    or Enums.CancellationDisposition.ConvertToDelivery
                    or Enums.CancellationDisposition.SaleVoided,
            _ => false,
        };
```

- [ ] **Step 3: Write the failing domain test**

Add to `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs` (open it first to match its existing `DeliveryReceipt.CreateDelivery`/`CreatePickup` construction helpers and assertion style):

```csharp
[Fact]
public void Cancel_AllowsSaleVoidedDispositionForDelivery()
{
    var dr = DeliveryReceipt.CreateDelivery(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S-0001", 1, DateOnly.FromDateTime(DateTime.UtcNow),
        "Recipient", "Address", null, null, Guid.NewGuid(), "Prepared By");

    dr.Cancel(Guid.NewGuid(), "Sale voided: test reason", CancellationDisposition.SaleVoided, DateTime.UtcNow);

    Assert.Equal(FulfillmentStatus.Cancelled, dr.Status);
    Assert.Equal(CancellationDisposition.SaleVoided, dr.CancellationDisposition);
}

[Fact]
public void Cancel_AllowsSaleVoidedDispositionForPickup()
{
    var dr = DeliveryReceipt.CreatePickup(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "S-0001", 1, DateOnly.FromDateTime(DateTime.UtcNow),
        "Recipient", null, null, Guid.NewGuid(), "Prepared By");

    dr.Cancel(Guid.NewGuid(), "Sale voided: test reason", CancellationDisposition.SaleVoided, DateTime.UtcNow);

    Assert.Equal(FulfillmentStatus.Cancelled, dr.Status);
    Assert.Equal(CancellationDisposition.SaleVoided, dr.CancellationDisposition);
}
```

Adjust the `DeliveryReceipt.CreateDelivery`/`CreatePickup` call signatures to match whatever the file's existing tests actually pass — read a neighboring existing test in the same file first and copy its exact argument list/order, since this plan's guess at the signature may not be pixel-perfect.

- [ ] **Step 4: Run to verify it fails, then passes**

Run: `dotnet test --filter "FullyQualifiedName~DeliveryReceiptEntityTests.Cancel_AllowsSaleVoided"`
Expected: FAIL before Step 2's edit (or if Step 2 already applied, this step is redundant — apply Step 2 first if you haven't), PASS after.

- [ ] **Step 5: Extract the transaction-less core from `CancelWithDispositionAsync`**

This is the structural heart of this task. Replace the current single `CancelWithDispositionAsync` method with two methods: a thin wrapper that owns the transaction (unchanged behavior for all 5 existing dispositions), and a core that does the actual work, taking an already-open `IDbContextTransaction` and never calling `BeginTransactionAsync`/`CommitAsync` itself — only `RollbackAsync` on a conflict, exactly as today, since rolling back is safe regardless of who began the transaction. This split exists because Task 3's void-triggered cascade must run inside `VoidSaleService`'s own transaction, not open a second, uncoordinated one on the same `ITenantDbContext`.

Replace the current method (verify the exact current line range yourself — it was ~436-593 as of this plan's writing, but earlier edits in this task may have shifted it):

```csharp
    private async Task<CancellationResultDto> CancelWithDispositionAsync(
        Guid id,
        FulfillmentMethod expectedMethod,
        string reason,
        CancellationDisposition disposition,
        FulfillmentMethod convertToMethod,
        bool completeReplacementImmediately,
        ReplacementFactory? buildReplacement,
        CancellationToken ct)
    {
        var tenantId = RequireTenant();

        // Tracked: dr.Cancel mutates it, and its RowVersion is the optimistic-concurrency token.
        var dr = await _db.DeliveryReceipts.Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.");
        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Fulfillment schedule not found.", ct);

        EnsureMethod(dr, expectedMethod);

        // This is what makes a Delivered delivery and a Claimed pickup un-cancellable and
        // un-convertible: both are terminal, and a terminal outcome is never rewritten.
        if (dr.Status != FulfillmentStatus.Pending)
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotPending,
                expectedMethod == FulfillmentMethod.Pickup
                    ? "Only a pending pickup can be cancelled."
                    : "Only a pending delivery can be cancelled.");
        }

        if (dr.SaleId is not { } saleId)
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotAllowed, "This schedule is not linked to a sale and has no quantities to release.");
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var result = await CancelWithDispositionCoreAsync(
            dr, transaction, tenantId, saleId, reason, disposition, expectedMethod, convertToMethod,
            completeReplacementImmediately, buildReplacement, nowUtc, ct);

        await transaction.CommitAsync(ct);
        return result;
    }

    /// <summary>
    /// The transactional guts shared by the public cancel-with-disposition path
    /// (<see cref="CancelWithDispositionAsync"/>) and the void-triggered cascade
    /// (<see cref="CancelActiveScheduleForVoidedSaleAsync"/>). Deliberately does NOT begin or commit
    /// <paramref name="transaction"/> — that stays the caller's responsibility, since the void path
    /// needs this to run inside a transaction it already owns and commits only after its own
    /// subsequent write (voiding the sale itself). On a conflict this still rolls the transaction back
    /// itself before throwing, exactly as before the split — that part is safe regardless of who
    /// began the transaction.
    /// <para>Cancelling never touches the Sale or its DeliveryCharge, and this schedule's own item rows
    /// are left exactly as they are — they stay forever as audit history. A schedule that is cancelled
    /// simply stops being active: <see cref="CreateScheduleAsync"/>'s active-schedule check only looks
    /// at non-Cancelled rows, so a future create is no longer blocked by this one.</para>
    /// </summary>
    private async Task<CancellationResultDto> CancelWithDispositionCoreAsync(
        DeliveryReceipt dr,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        Guid tenantId,
        Guid saleId,
        string reason,
        CancellationDisposition disposition,
        FulfillmentMethod expectedMethod,
        FulfillmentMethod convertToMethod,
        bool completeReplacementImmediately,
        ReplacementFactory? buildReplacement,
        DateTime nowUtc,
        CancellationToken ct)
    {
        // The same lock every allocating path takes, so a conversion can never race an allocation.
        await LockSaleItemsAsync(tenantId, saleId, ct);

        // A concurrent mark-delivered / claim / cancel may have committed while we waited on the lock —
        // the status checked before entering this core is a pre-lock read and can already be stale.
        var currentStatus = await _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.Id == dr.Id)
            .Select(d => d.Status)
            .SingleAsync(ct);
        if (currentStatus != FulfillmentStatus.Pending)
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was changed by someone else. Please refresh and try again.");
        }

        // A disposition that builds a replacement is creating a brand-new schedule, so it must clear the
        // SAME sale-status gate CreateScheduleAsync does. SaleVoided never builds a replacement, so it
        // takes the else branch below and is never blocked by this gate — correct, since by the time
        // this runs the sale may already be (or is about to become) Voided.
        Sale sale;
        if (buildReplacement is not null)
        {
            sale = await LoadFulfillableSaleAsync(tenantId, saleId, ct);
        }
        else
        {
            sale = await _db.Sales.AsNoTracking()
                .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
                ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        }

        // TRACKED on purpose — ConvertFulfillment mutates these rows, and it is the only mutator either
        // intent column has after checkout.
        var saleItems = await _db.SaleItems
            .Where(i => i.TenantId == tenantId && i.SaleId == saleId)
            .ToListAsync(ct);

        // The domain re-validates the method/disposition pairing; take-now is never accepted.
        dr.Cancel(_currentUser.UserId, reason, disposition, nowUtc);

        DeliveryReceipt? replacement = null;
        if (buildReplacement is not null)
        {
            var preparedByName = await ResolvePreparedByNameAsync(ct);
            var sequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, convertToMethod, ct);
            replacement = buildReplacement(sale, sequenceNumber, preparedByName);

            foreach (var item in dr.Items.OrderBy(i => i.CreatedAtUtc).ThenBy(i => i.Id))
            {
                replacement.AddItem(
                    item.SaleItemId, item.ProductNameSnapshot, item.VariantNameSnapshot, item.Quantity, item.UnitPrice);
            }

            if (completeReplacementImmediately)
            {
                replacement.MarkClaimed(_currentUser.UserId, nowUtc);
            }

            _db.DeliveryReceipts.Add(replacement);
        }

        foreach (var item in dr.Items)
        {
            if (convertToMethod != expectedMethod)
            {
                var saleItem = saleItems.SingleOrDefault(i => i.Id == item.SaleItemId)
                    ?? throw new BusinessRuleException(
                        ErrorCodes.InvalidSaleItem, "This schedule refers to an item that is no longer on its sale.");
                saleItem.ConvertFulfillment(expectedMethod, convertToMethod, item.Quantity);
            }

            _db.FulfillmentConversions.Add(FulfillmentConversion.Record(
                tenantId, saleId, item.SaleItemId, item.Quantity, expectedMethod, convertToMethod,
                sourceRecordId: dr.Id, replacementRecordId: replacement?.Id,
                reason, _currentUser.UserId, nowUtc));
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was changed by someone else. Please refresh and try again.");
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This schedule was already cancelled by someone else. Please refresh and try again.");
        }

        var cancelledDto = await MapToDtoAsync(dr, ct);
        var replacementDto = replacement is null ? null : await MapToDtoAsync(replacement, ct);
        return new CancellationResultDto(cancelledDto, replacementDto);
    }

    /// <summary>
    /// Cancels the sale's active Pending fulfillment schedule, if it has one, as part of voiding the
    /// sale. A no-op when the sale has no Pending schedule — the common case, since most voided sales
    /// are Take-now. Deliberately scoped to Pending only: a schedule that already reached a terminal
    /// state (Delivered/Claimed) is left untouched, since voiding a sale after its delivery/pickup
    /// already happened is a different, unhandled scenario outside this fix's scope.
    /// <para>Must run inside <paramref name="transaction"/>, a transaction the CALLER (VoidSaleService)
    /// already began on the same <see cref="ITenantDbContext"/> — this method never begins or commits
    /// one itself, since the caller commits only after its own subsequent write. Disposition is always
    /// <see cref="CancellationDisposition.SaleVoided"/>, a value deliberately excluded from
    /// <c>DeliveryReceiptValidators</c>'s allowed-dispositions lists so a cashier can never select it
    /// through the public cancel endpoints — this method is the only caller that may ever pass it.</para>
    /// </summary>
    public async Task CancelActiveScheduleForVoidedSaleAsync(
        Guid saleId, string voidReason, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken ct = default)
    {
        var tenantId = RequireTenant();

        var dr = await _db.DeliveryReceipts.Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.SaleId == saleId
                && d.Status == FulfillmentStatus.Pending, ct);
        if (dr is null)
        {
            return;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;

        await CancelWithDispositionCoreAsync(
            dr, transaction, tenantId, saleId, $"Sale voided: {voidReason}", CancellationDisposition.SaleVoided,
            expectedMethod: dr.Method, convertToMethod: dr.Method,
            completeReplacementImmediately: false, buildReplacement: null, nowUtc, ct);
    }
```

Add `using Microsoft.EntityFrameworkCore.Storage;` to this file's usings if it isn't already present, and use the plain `IDbContextTransaction` type name in the signatures above instead of the fully-qualified form once the using is in place (the fully-qualified form above is only to make this plan text unambiguous — write the actual code with the shorter, idiomatic name).

- [ ] **Step 6: Add the new method to the interface**

In `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs`, add to `IDeliveryReceiptService`:

```csharp
    Task CancelActiveScheduleForVoidedSaleAsync(
        Guid saleId, string voidReason, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken ct = default);
```

(Add the `using Microsoft.EntityFrameworkCore.Storage;` to this file too, then use the short name.)

- [ ] **Step 7: Run the full existing cancellation/delivery/pickup test suite to confirm zero regression**

Run: `dotnet test --filter "FullyQualifiedName~DeliveryReceipt|FullyQualifiedName~Pickup|FullyQualifiedName~FulfillmentConversion"`
Expected: every existing test for the 5 pre-existing dispositions still passes, byte-for-byte, since the refactor only moved code, it did not change any of it. If anything fails, the refactor introduced a behavioral difference — find and fix it before proceeding; do not proceed with a red suite.

- [ ] **Step 8: Write a failing integration test for the new cascade method itself**

Add to `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs` (open it first to copy its exact seeding/checkout helper conventions — it already knows how to seed a tenant, complete a Delivery checkout, and open a raw `IDbContextTransaction` for concurrency testing, all patterns this test needs):

```csharp
[Fact]
public async Task CancelActiveScheduleForVoidedSaleAsync_CancelsThePendingSchedule()
{
    var sale = await CheckoutDeliverySaleAsync(); // reuse this file's existing Delivery-checkout helper
    var scheduleBefore = await _deliveryReceiptService.GetSaleFulfillmentAsync(sale.Id, CancellationToken.None);
    Assert.NotNull(scheduleBefore.ActiveSchedule);
    Assert.Equal("Pending", scheduleBefore.ActiveSchedule!.Status.ToString());

    await using var transaction = await _db.Database.BeginTransactionAsync(CancellationToken.None);
    await _deliveryReceiptService.CancelActiveScheduleForVoidedSaleAsync(
        sale.Id, "Test void reason", transaction, CancellationToken.None);
    await transaction.CommitAsync(CancellationToken.None);

    var scheduleAfter = await _deliveryReceiptService.GetSaleFulfillmentAsync(sale.Id, CancellationToken.None);
    Assert.Null(scheduleAfter.ActiveSchedule);
    Assert.Single(scheduleAfter.Deliveries);
    Assert.Equal("Cancelled", scheduleAfter.Deliveries[0].Status.ToString());

    var conversions = scheduleAfter.Conversions;
    Assert.Contains(conversions, c => c.Reason.Contains("Sale voided"));
}

[Fact]
public async Task CancelActiveScheduleForVoidedSaleAsync_IsNoOpWhenNoActiveSchedule()
{
    var sale = await CheckoutTakeNowSaleAsync(); // reuse this file's (or an adjacent file's) Take-now helper

    await using var transaction = await _db.Database.BeginTransactionAsync(CancellationToken.None);
    await _deliveryReceiptService.CancelActiveScheduleForVoidedSaleAsync(
        sale.Id, "Test void reason", transaction, CancellationToken.None);
    await transaction.CommitAsync(CancellationToken.None); // must not throw even though there was nothing to cancel

    var schedule = await _deliveryReceiptService.GetSaleFulfillmentAsync(sale.Id, CancellationToken.None);
    Assert.Null(schedule.ActiveSchedule);
    Assert.Empty(schedule.Deliveries);
    Assert.Empty(schedule.Pickups);
}
```

Adjust helper method names/return shapes to match whatever this file (or `PickupTests.cs`/`CheckoutDeliveryFulfillmentTests.cs`) actually already provides — read the file first rather than assuming these exact helper names exist; if no matching helper exists, write the minimal checkout call inline using this file's existing HTTP-client/seeding conventions rather than inventing a new abstraction.

- [ ] **Step 9: Run to verify these fail, then pass**

Run: `dotnet test --filter "FullyQualifiedName~CancelActiveScheduleForVoidedSaleAsync"`
Expected: FAIL before Steps 5-6 are applied (compile error, method doesn't exist) — apply Steps 5-6 first if you're reading this out of order — then PASS.

- [ ] **Step 10: Run the full backend suite**

Run: `dotnet test`. Expected: 500/500 (498 from Task 1 + 2 domain tests + 2 integration tests from this task — adjust the exact expected count if Task 1 added a different number, or if you found additional pre-existing tests needed adjustment during Step 7).

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(delivery): add SaleVoided disposition and extract a reusable, transaction-less cancel-with-disposition core"
```

---

### Task 3: Backend — `VoidSaleService` cascades into the fulfillment schedule

**Files:**
- Modify: `src/Negosio.Application/Sales/VoidSaleService.cs`
- Test: `tests/Negosio.UnitTests/Sales/SaleVoidTests.cs` (if it covers `VoidEligibility`/pure logic — check first) and/or `tests/Negosio.IntegrationTests/` (wherever the existing end-to-end void tests live — search for `VoidAsync` callers in `tests/Negosio.IntegrationTests/` to find the right file; if none exists yet for full void-flow integration tests, create `tests/Negosio.IntegrationTests/Sales/VoidSaleFulfillmentTests.cs`)

**Interfaces:**
- Consumes: `IDeliveryReceiptService.CancelActiveScheduleForVoidedSaleAsync(Guid, string, IDbContextTransaction, CancellationToken)` from Task 2.
- Produces: nothing new for later tasks — this is the last backend task in this plan.

**Critical ordering requirement, read before writing any code:** the fulfillment cascade MUST be called **before** `sale.Void(...)` is invoked, not after. `sale` is a tracked entity; if `sale.Void(...)` runs first, its change becomes pending in the same `ITenantDbContext`'s change tracker, and the cascade's own `SaveChangesAsync()` call (inside `CancelWithDispositionCoreAsync`, from Task 2) would then flush that pending Sale change too — meaning a genuine Sale-level concurrency conflict (e.g., a competing Return committing between this method's load and write) would surface through the cascade's own `catch (DbUpdateConcurrencyException)` block, producing "This schedule was changed by someone else" for what is actually a Sale-row conflict having nothing to do with any schedule. Calling the cascade first avoids this: at that point nothing is pending for `sale` yet, so the cascade's `SaveChangesAsync()` only ever flushes its own schedule/conversion changes, and `VoidSaleService`'s own later `SaveChangesAsync()` correctly owns catching any `Sale.RowVersion` conflict with its own correct message.

Read `src/Negosio.Application/Sales/VoidSaleService.cs` in full before editing — this task touches its main `VoidAsync` method between the authoritative post-lock `EnsureEligibleAsync` call and the inventory-reversal loop.

- [ ] **Step 1: Write the failing integration test**

Create or extend the integration test file with (adjust helper names to match this codebase's actual existing void-testing conventions — search for how existing void tests seed a completed sale and call the void endpoint/service):

```csharp
[Fact]
public async Task VoidAsync_CancelsAnActivePendingDeliverySchedule()
{
    var sale = await CheckoutDeliverySaleAsync(); // existing helper, reuse from wherever delivery checkout tests already live

    await _voidSaleService.VoidAsync(sale.Id, new VoidSaleRequest("Customer changed their mind", Approval: null), CancellationToken.None);

    var fulfillment = await _deliveryReceiptService.GetSaleFulfillmentAsync(sale.Id, CancellationToken.None);
    Assert.Null(fulfillment.ActiveSchedule);
    Assert.Single(fulfillment.Deliveries);
    Assert.Equal("Cancelled", fulfillment.Deliveries[0].Status.ToString());

    // The schedule can no longer be completed — CompleteAsync's existing Pending-only gate now applies.
    await Assert.ThrowsAsync<BusinessRuleException>(
        () => _deliveryReceiptService.MarkDeliveredAsync(fulfillment.Deliveries[0].Id, CancellationToken.None));
}

[Fact]
public async Task VoidAsync_CancelsAnActivePendingPickupSchedule()
{
    var sale = await CheckoutPickupSaleAsync(); // existing helper

    await _voidSaleService.VoidAsync(sale.Id, new VoidSaleRequest("Customer changed their mind", Approval: null), CancellationToken.None);

    var fulfillment = await _deliveryReceiptService.GetSaleFulfillmentAsync(sale.Id, CancellationToken.None);
    Assert.Null(fulfillment.ActiveSchedule);
    Assert.Single(fulfillment.Pickups);
    Assert.Equal("Cancelled", fulfillment.Pickups[0].Status.ToString());
}

[Fact]
public async Task VoidAsync_StillVoidsATakeNowSaleWithNoSchedule()
{
    // Regression guard: the cascade must be a true no-op for the common case.
    var sale = await CheckoutTakeNowSaleAsync(); // existing helper

    var result = await _voidSaleService.VoidAsync(sale.Id, new VoidSaleRequest("Test", Approval: null), CancellationToken.None);

    Assert.Equal(SaleStatus.Voided, result.Sale.Status);
}
```

Adjust `VoidSaleRequest`'s exact constructor signature to match its real current shape (read `VoidSaleContracts.cs` or wherever it's defined) — this plan's guess at `(string reason, ApprovalInput? approval)` may not be exact.

- [ ] **Step 2: Run to verify the first two fail**

Run: `dotnet test --filter "FullyQualifiedName~VoidAsync_CancelsAnActive"`
Expected: FAIL — the schedule remains Pending after void, since the cascade doesn't exist yet.

- [ ] **Step 3: Inject `IDeliveryReceiptService` and wire the cascade**

In `src/Negosio.Application/Sales/VoidSaleService.cs`, add the dependency:

```csharp
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<VoidSaleRequest> _validator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IVoidAuthorizationResolver _authResolver;
    private readonly IInventoryPosting _inventory;
    private readonly ISaleQueryService _saleQuery;
    private readonly IDeliveryReceiptService _deliveryReceipts;
    private readonly TimeProvider _timeProvider;

    public VoidSaleService(
        ITenantDbContext db, ICurrentUser currentUser, IValidator<VoidSaleRequest> validator,
        IBranchAccessResolver branchAccess, IVoidAuthorizationResolver authResolver,
        IInventoryPosting inventory, ISaleQueryService saleQuery, IDeliveryReceiptService deliveryReceipts,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
        _branchAccess = branchAccess;
        _authResolver = authResolver;
        _inventory = inventory;
        _saleQuery = saleQuery;
        _deliveryReceipts = deliveryReceipts;
        _timeProvider = timeProvider;
    }
```

Then, inside `VoidAsync`, insert the cascade call immediately after the authoritative post-lock `EnsureEligibleAsync` call and before the inventory-reversal loop (per the ordering requirement above — before `sale.Void(...)`, not after):

```csharp
        // Authoritative re-check, now that the session row is locked for the rest of this transaction.
        await EnsureEligibleAsync(sale, tenantId, nowUtc, cancellationToken);

        // A Pending delivery/pickup schedule on this sale must not survive the sale being voided as an
        // orphaned, unstoppable-but-uncancellable row. A no-op for the common case (Take-now sales, or
        // any sale with no active schedule). MUST run before sale.Void(...) below — see this task's
        // ordering note for why (a Sale-level concurrency conflict must surface through THIS method's
        // own SaveChangesAsync/catch block below, not get intercepted by the cascade's).
        await _deliveryReceipts.CancelActiveScheduleForVoidedSaleAsync(
            sale.Id, request.Reason, transaction, cancellationToken);

        var lineVariantIds = sale.Items.Select(i => i.ProductVariantId).ToList();
```

(The rest of the method — inventory reversal, `sale.Void(...)`, the `try`/`catch (DbUpdateConcurrencyException)`/commit — stays exactly as it is today; only the one new call above is inserted.)

Register the DI binding if this codebase wires constructors explicitly rather than relying on convention-based auto-registration — check `Program.cs`/the DI extension methods for how `IVoidSaleService`/`IDeliveryReceiptService` are currently registered and confirm `VoidSaleService`'s new constructor parameter resolves correctly (it almost certainly does automatically if both are already registered in the same DI container, but verify by running the app or the integration tests rather than assuming).

- [ ] **Step 4: Run to verify all three tests pass**

Run: `dotnet test --filter "FullyQualifiedName~VoidAsync_CancelsAnActive|FullyQualifiedName~VoidAsync_StillVoidsATakeNow"`
Expected: PASS for all three.

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test`. Expected: 503/503 (500 from Task 2 + 3 new tests here), 0 failures. Pay particular attention to every existing `SaleVoidTests`/void-related integration test still passing unchanged — this task's edit sits inside the most concurrency-sensitive method in the sales domain, and a subtle ordering mistake would most likely show up as a pre-existing void test now failing or flaking.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "fix(sales): voiding a sale now cancels its active Pending fulfillment schedule instead of stranding it"
```

---

### Task 4: Frontend — recovery UI on Sale Detail for a failed post-checkout schedule call

**Files:**
- Modify: `web/negosio-web/src/api/types.ts` (mirror Task 1's new `pickupRequiredQuantity` field on `SaleItemDto`)
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`

**Interfaces:**
- Consumes: `SaleItemDto.deliveryRequiredQuantity`/`pickupRequiredQuantity` (Task 1's backend field, now mirrored here), `SaleFulfillmentSummaryDto.activeSchedule` (already exists), `fulfillmentApi.createDelivery(saleId, body)`/`createPickup(saleId, body)` (already exists, used today only by `PosTerminal.tsx`), `FulfillmentDetailsFields` component (already exists, the flat single-schedule form built by the prior simplification plan).
- Produces: nothing consumed by a later task in this plan.

Read the current `SaleDetailPage.tsx`'s Fulfillment section (search for `Fulfillment` — it's a single `{summary && (...)}` block) and `FulfillmentDetailsFields.tsx`'s current `Props` shape (`method`, `values`, `onChange`, `deliveryCharge?`, `onDeliveryChargeChange?`, `errors`, `attempted`, `disabled?`) in full before editing.

- [ ] **Step 1: Mirror the new backend field**

In `web/negosio-web/src/api/types.ts`, find `SaleItemDto` and add the field mirroring the backend change exactly:

```typescript
export interface SaleItemDto {
  id: string
  productVariantId: string
  productName: string
  variantName: string | null
  sku: string | null
  barcode: string | null
  unitPrice: number
  quantity: number
  grossAmount: number
  discountAmount: number
  taxAmount: number
  netAmount: number
  costPriceSnapshot: number | null
  returnedQuantity: number
  deliveryRequiredQuantity: number
  pickupRequiredQuantity: number
}
```

- [ ] **Step 2: Detect the "needs scheduling" state**

In `SaleDetailPage.tsx`, inside the existing `{summary && (...)}` block, before the existing `(summary.deliveries.length > 0 || summary.pickups.length > 0) && (...)` conditional, compute whether the sale needs a schedule it doesn't have:

```typescript
const needsDelivery = d.items.some((i) => i.deliveryRequiredQuantity > 0)
const needsPickup = d.items.some((i) => i.pickupRequiredQuantity > 0)
const needsSchedulingMethod: 'Delivery' | 'Pickup' | null =
  summary.activeSchedule == null && needsDelivery ? 'Delivery' : summary.activeSchedule == null && needsPickup ? 'Pickup' : null
```

(`d` is this page's existing sale-detail data variable, already in scope in the surrounding code — confirm its exact name by reading the enclosing function; this plan uses `d` because that's what the existing `d.returns.length > 0` check nearby already uses.)

- [ ] **Step 3: Override the status badge when a schedule is needed but missing**

Immediately replace the existing badge rendering:

```tsx
<Badge tone={saleFulfillmentStatusTone(summary.fulfillmentStatus)}>
  {SALE_FULFILLMENT_STATUS_LABELS[summary.fulfillmentStatus]}
</Badge>
```

with a version that shows the true state rather than the misleading `TakeNow` label when a schedule is missing:

```tsx
<Badge tone={needsSchedulingMethod ? 'warning' : saleFulfillmentStatusTone(summary.fulfillmentStatus)}>
  {needsSchedulingMethod
    ? `Needs ${needsSchedulingMethod === 'Delivery' ? 'delivery' : 'pickup'} scheduling`
    : SALE_FULFILLMENT_STATUS_LABELS[summary.fulfillmentStatus]}
</Badge>
```

(Use whichever `Badge` tone name this codebase's `Badge` component actually accepts for a warning/attention state — check its type definition; `'warning'` matches the tone already used elsewhere in this same page for `saleFulfillmentStatusTone`'s `PendingDelivery`/`PendingPickup` results, so it should already be a valid tone value.)

- [ ] **Step 4: Add the recovery form**

Add local state for the recovery form near this page's other `useState` calls (e.g. near `cancelTarget`/`deliverTarget`):

```typescript
const [schedulingOpen, setSchedulingOpen] = useState(false)
const [schedulingFields, setSchedulingFields] = useState<FulfillmentDetails>(emptyFulfillmentDetails())
const [schedulingAttempted, setSchedulingAttempted] = useState(false)
```

(`FulfillmentDetails`/`emptyFulfillmentDetails` already exist in `web/negosio-web/src/lib/pos.ts` — import them, do not redefine.)

Add the create-schedule mutations near this page's other mutations:

```typescript
const createDeliveryMutation = useMutation({
  mutationFn: (fields: FulfillmentDetails) =>
    fulfillmentApi.createDelivery(d.sale.id, {
      scheduledDate: fields.scheduledDate,
      recipientName: fields.recipientName.trim(),
      deliveryAddress: fields.deliveryAddress.trim(),
      contactNumber: fields.contactNumber.trim() || null,
      notes: fields.notes.trim() || null,
    }),
  onSuccess: () => {
    setSchedulingOpen(false)
    invalidateFulfillment()
  },
})

const createPickupMutation = useMutation({
  mutationFn: (fields: FulfillmentDetails) =>
    fulfillmentApi.createPickup(d.sale.id, {
      scheduledDate: fields.scheduledDate,
      recipientName: fields.recipientName.trim(),
      contactNumber: fields.contactNumber.trim() || null,
      notes: fields.notes.trim() || null,
    }),
  onSuccess: () => {
    setSchedulingOpen(false)
    invalidateFulfillment()
  },
})
```

(`invalidateFulfillment` already exists on this page, per the existing `Mark delivered`/`Mark claimed`/`Cancel` mutations — reuse it, don't write a second invalidation helper. Confirm `fulfillmentApi.createDelivery`/`createPickup`'s exact request-body field names against `web/negosio-web/src/api/fulfillment.ts` and `CreateDeliveryReceiptRequest`/`CreatePickupRequest` in `types.ts` before finalizing — this plan's field list is derived from the prior simplification plan's own final state, but verify it against the live file.)

Render the recovery affordance right where `needsSchedulingMethod` is true, inside the `{summary && (...)}` block, replacing/alongside the existing history-list conditional:

```tsx
{needsSchedulingMethod && (
  <div className="space-y-3 rounded-lg border border-warning-300 bg-warning-50 p-4">
    <p className="text-sm text-text-secondary">
      This sale was completed for {needsSchedulingMethod.toLowerCase()}, but scheduling it failed at checkout time.
      Schedule it now to avoid losing track of this order.
    </p>
    {!schedulingOpen ? (
      <Button variant="secondary" onClick={() => setSchedulingOpen(true)}>
        Schedule now
      </Button>
    ) : (
      <>
        <FulfillmentDetailsFields
          method={needsSchedulingMethod}
          values={schedulingFields}
          onChange={(patch) => setSchedulingFields((prev) => ({ ...prev, ...patch }))}
          errors={{}}
          attempted={schedulingAttempted}
        />
        <div className="flex gap-2">
          <Button
            onClick={() => {
              setSchedulingAttempted(true)
              if (!schedulingFields.scheduledDate || !schedulingFields.recipientName.trim()) return
              if (needsSchedulingMethod === 'Delivery' && !schedulingFields.deliveryAddress.trim()) return
              if (needsSchedulingMethod === 'Delivery') createDeliveryMutation.mutate(schedulingFields)
              else createPickupMutation.mutate(schedulingFields)
            }}
            loading={createDeliveryMutation.isPending || createPickupMutation.isPending}
          >
            Confirm schedule
          </Button>
          <Button variant="secondary" onClick={() => setSchedulingOpen(false)}>
            Cancel
          </Button>
        </div>
        {(createDeliveryMutation.isError || createPickupMutation.isError) && (
          <p className="text-sm text-danger-600">
            Could not create the schedule — it may already exist (try refreshing) or the details need
            correction above.
          </p>
        )}
      </>
    )}
  </div>
)}
```

Match this codebase's actual `Button`/color-token conventions exactly (check an existing usage on this same page, e.g. the `Mark delivered` confirm button, for the real prop names — `loading` vs `isLoading`, `variant` values, etc. — rather than trusting this plan's guess verbatim).

- [ ] **Step 5: Run `tsc -b` and `npm run build`**

Run: `cd web/negosio-web && npx tsc -b && npm run build`
Expected: zero errors from both.

- [ ] **Step 6: Manual/live verification**

Start the API (`ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5170 dotnet run` from `src/Negosio.Api`) and the frontend (`npm run dev` from `web/negosio-web`). Seed a Delivery checkout, then simulate the failure by manually creating a second delivery-receipts POST for the same sale via a raw HTTP call (curl/fetch) BEFORE the frontend's own automatic call would run — or, more directly, complete a Delivery checkout normally, let its schedule succeed, then manually null out/delete that `DeliveryReceipt` row in the LocalDB via a raw SQL `DELETE` for test purposes only (never do this against real data) to reproduce the "sale needs delivery, no schedule" state, then reload Sale Detail and confirm: the badge reads "Needs delivery scheduling" (not "Take now"), the "Schedule now" button appears, filling the form and confirming creates the schedule, the badge updates to "Pending delivery" and the history list appears, and a second click of "Schedule now" (if the button is still reachable) fails gracefully with the guard's rejection rather than creating a duplicate.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(web): recovery UI on Sale Detail when a post-checkout schedule call fails"
```

---

### Task 5: Frontend — verify/update the post-checkout error toast wording

**Files:**
- Modify (if needed): `web/negosio-web/src/components/pos/PosTerminal.tsx`

**Interfaces:**
- Consumes: Task 4's new recovery UI (this task only needs to confirm the toast's wording accurately describes it).

- [ ] **Step 1: Read the current toast text**

Read `PosTerminal.tsx`'s `createDeliveryMutation`/`createPickupMutation` `onError` handlers (search for `"could not be scheduled"`).

- [ ] **Step 2: Update if inaccurate**

The current wording is approximately: `` `Sale #${variables.saleNumber} completed, but the delivery could not be scheduled. Schedule it from the sale's detail page.` ``. After Task 4, this is now accurate (Sale Detail genuinely has a "Schedule now" action) — if the actual current wording differs from this or is vaguer, tighten it to name the action precisely, e.g.:

```typescript
onError: (_err, variables) => {
  toast(
    'error',
    `Sale #${variables.saleNumber} completed, but the delivery could not be scheduled. Open the sale's detail page and use "Schedule now" to finish setting it up.`,
  )
}
```

Mirror the same change for the pickup mutation's `onError`, swapping "delivery"/"Schedule now" wording appropriately (reuse the exact same "Schedule now" label Task 4 used for the button, so the toast's instruction matches the UI verbatim).

- [ ] **Step 3: Run `tsc -b`**

Run: `npx tsc -b` from `web/negosio-web`. Expected: zero errors (this is a string-only change, should not affect types).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "fix(web): point the post-checkout scheduling-failure toast at the new Schedule now action"
```

---

### Task 6: Final verification for this plan

**Files:** none unless a defect is found.

- [ ] **Step 1: Full backend suite**

Run: `dotnet test` from the repo root. Record the exact pass count (expected: 503/503 per this plan's running total — adjust if any task's step count differed during execution).

- [ ] **Step 2: Full frontend build**

Run: `cd web/negosio-web && npx tsc -b && npm run build`. Expected: zero errors from both.

- [ ] **Step 3: Live-verify both fixes end to end**

Fix 1: complete a Delivery checkout normally through the real UI; confirm no recovery UI appears (the happy path is unaffected). Then reproduce a schedule-creation failure (see Task 4 Step 6's method, or a more realistic one if you find one — e.g. temporarily disabling the network tab's response for that one call in browser devtools) and confirm the full recovery flow works as designed.

Fix 2: complete a Delivery checkout, confirm the schedule is Pending, void the sale, and confirm: the schedule shows Cancelled with disposition `SaleVoided` (check via `GET /api/sales/{id}/fulfillment` or the Sale Detail page's conversion history), attempting `Mark delivered` on it now fails, and the sale is correctly excluded from the Deliveries report (unchanged, pre-existing behavior — just confirm it still holds). Repeat for a Pickup sale.

- [ ] **Step 4: Write a short summary**

Do not merge, do not push. Report: exact test counts (before/after), confirmation both fixes work live, and any deviation from this plan's exact code (a signature that didn't match, a helper name that was different) worth noting for whoever reads this plan later.
