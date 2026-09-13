# Pickup Fulfillment + Cross-Method Conversions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Pickup as a third fulfillment method alongside Take-now and Delivery, with per-method quantity allocation at checkout, Pickup scheduling/claiming, disposition-driven cancellation that can atomically convert quantities between Delivery and Pickup, an immutable conversion audit trail, and three fulfillment report views.

**Architecture:** Generalize the **existing** `DeliveryReceipt` aggregate in place with a `FulfillmentMethod` discriminator — no table rename, no row migration, no rewrite of the shipped delivery paths. One schedule table means one allocation engine, one pessimistic lock, one idempotency mechanism, one conversion path, and one table behind the combined report. `SaleItem` gains `PickupRequiredQuantity` beside the existing `DeliveryRequiredQuantity`; `TakeNowQuantity` becomes `Quantity − Delivery − Pickup`. Post-sale conversions mutate those two intent columns **only** through an immutable `FulfillmentConversion` event, which is what keeps the original checkout allocation reconstructable.

**Tech Stack:** .NET 9 / EF Core 9 / SQL Server (LocalDB), FluentValidation, xUnit + FluentAssertions on the backend; React 19 / TypeScript (strict) / Vite / TanStack Query on the frontend (no frontend test runner in this repo — verify with `npx tsc -b`, never a bare `--noEmit`, which silently checks zero files under this repo's TS project-references setup).

**Spec:** `docs/superpowers/specs/2026-09-13-pickup-fulfillment.md` — read it alongside this plan. Where the two disagree, the spec wins.

## Global Constraints

- **One schedule table, discriminated by `Method`.** `DeliveryReceipt`/`DeliveryReceiptItem` keep their table names (renaming is pure risk for zero functional gain) and gain a `FulfillmentMethod Method` column. Existing rows are all `Delivery`. Every delivery-only query gains `.Where(d => d.Method == FulfillmentMethod.Delivery)`; every pickup-only query the mirror. **There is exactly one source of truth for every quantity** — never add a parallel pickup table.
- **`FulfillmentStatus.Unscheduled` is never persisted on a schedule row.** A row is always `Pending`, `Completed`, or `Cancelled`. `Unscheduled` describes *quantity not yet on any schedule* and exists for the UI/report vocabulary only (matches the spec's own label table: `Delivery | Unscheduled | "Deliver later"`). Do not add an `Unscheduled` row state.
- **Intent columns are mutable only through an audited conversion.** `SaleItem.DeliveryRequiredQuantity` and `SaleItem.PickupRequiredQuantity` may only change via a domain method that a `FulfillmentConversion` event is written alongside, in the same transaction. No other write path, no setter, no direct assignment in a service. The original checkout allocation is reconstructed as *current intent − replayed conversions*, which is why no duplicate "original" columns are added.
- **`TakeNow` is checkout-only.** It is never a `CancellationDisposition`, never a conversion target (`ToMethod`), and never appears on a schedule row. `Quantity − DeliveryRequiredQuantity − PickupRequiredQuantity` is the take-now amount, always already completed.
- **Valid disposition pairs are enforced server-side**: Delivery → `DeliverLater` | `ConvertToPickup` | `CustomerPickedUpInstead`; Pickup → `PickupLater` | `ConvertToDelivery`. Any other pairing is a `BusinessRuleException`, not a silent no-op.
- **`Sales.DeliveryCharge` is never touched by this plan's logic.** Read-only everywhere; never copied onto a schedule; never multiplied by schedule count; never altered by a cancellation or a conversion — including a Delivery→Pickup conversion on a sale that paid a delivery charge. Reports aggregate it by **distinct Sale**.
- **Concurrency:** the existing `LockSaleItemsAsync` (`WITH (UPDLOCK, HOLDLOCK)` on the sale's `SaleItems` rows) already serializes *all* allocation for a sale and is method-agnostic — reuse it unchanged for every allocating operation, including conversions. RowVersion + `DbUpdateConcurrencyException` → explicit `RollbackAsync` **before any re-read** → `ConflictException` remains the idiom for status transitions on an existing row.
- **Cancellation + conversion + replacement creation is one transaction.** A failed replacement must roll back the cancellation and the conversion. Retrying must not duplicate the replacement or the conversion event — reuse the existing `BatchRequestId`-style client-supplied idempotency key pattern where a record is created.
- **HTTP verbs:** every state-changing action is `POST /{id}/{verb}`. This codebase never uses `PATCH`; `PUT` is reserved for pure field replacement.
- **Business "today"** comes from `ReportPeriodResolver.BusinessOffset` (UTC+8) via the injected `TimeProvider` — never `DateTime.UtcNow` directly for a date comparison, and never re-declare the offset.
- **Tenant isolation** on every query; branch scope via the existing `GuardBranchAsync` (404, not 403) on every read and every state change.
- **Existing behavior must keep working**: existing delivery records stay valid, existing delivery reports keep working, existing tests keep passing. A regression in the shipped delivery feature is a Critical defect, not a tradeoff.
- Do not merge, do not push — stay on `feature/pos-for-delivery`. Commit after every green step.

---

## File Structure

**Domain** (`src/Negosio.Domain/`)
- `Enums/FulfillmentMethod.cs` *(new)* — `TakeNow=1, Delivery=2, Pickup=3`.
- `Enums/FulfillmentStatus.cs` *(new)* — `Unscheduled=1, Pending=2, Completed=3, Cancelled=4`. Replaces `DeliveryStatus` (deleted).
- `Enums/CancellationDisposition.cs` *(new)* — the 5 dispositions.
- `Entities/Delivery/DeliveryReceipt.cs` — gains `Method`, `CancellationDisposition`, generic `ScheduledDate`/`CompletedAtUtc`/`CompletedByUserId`/`Notes`, nullable `DeliveryAddress`, a `CreatePickup` factory and `MarkClaimed`.
- `Entities/Delivery/FulfillmentConversion.cs` *(new)* — the immutable audit event.
- `Entities/Sales/SaleItem.cs` — gains `PickupRequiredQuantity`, a corrected `TakeNowQuantity`, and the single audited `ConvertFulfillment` mutator.

**Infrastructure** (`src/Negosio.Infrastructure/`)
- `Persistence/Configurations/DeliveryReceiptConfiguration.cs` — `Method` column, method-scoped unique index, nullable address.
- `Persistence/Configurations/FulfillmentConversionConfiguration.cs` *(new)*.
- `Persistence/TenantDbContext.cs` + `ITenantDbContext` — new `DbSet<FulfillmentConversion>`.
- One new migration.

**Application** (`src/Negosio.Application/`)
- `Delivery/DeliveryReceiptContracts.cs` — pickup DTOs, disposition inputs, conversion DTOs, extended service interface.
- `Delivery/DeliveryReceiptValidators.cs` — pickup + disposition validators.
- `Delivery/DeliveryReceiptService.cs` — method-aware availability, pickup create/claim, disposition-driven cancel with atomic conversion.
- `Delivery/SaleFulfillmentCalculator.cs` — the sale-level `SaleFulfillmentStatus` states.
- `Pos/CheckoutContracts.cs` / `Pos/CheckoutValidator.cs` / `Pos/CheckoutService.cs` — 3-way allocation.
- `Reports/ReportsContracts.cs` / `ReportsService.cs` — delivery (filtered), pickup, combined.

**Api** (`src/Negosio.Api/`)
- `Controllers/SalesController.cs`, `Controllers/DeliveryReceiptsController.cs`, `Controllers/ReportsController.cs`, `Authorization/AuthorizationPolicies.cs`.

**Frontend** (`web/negosio-web/src/`) — `api/types.ts`, `api/deliveryReceipts.ts` (renamed to `api/fulfillment.ts`), `api/reports.ts`, `lib/pos.ts`, `lib/useCan.ts`, POS components, `pages/SaleDetailPage.tsx`, sales modals, `pages/DeliveryReceiptPage.tsx`, `pages/DeliveryReportsPage.tsx`.

---

## Task 1: Fulfillment enums — replace `DeliveryStatus`, add `FulfillmentMethod` and `CancellationDisposition`

This is the broad-but-mechanical rename, isolated into its own task so it is reviewable on its own. It changes no behavior.

**Files:**
- Create: `src/Negosio.Domain/Enums/FulfillmentMethod.cs`
- Create: `src/Negosio.Domain/Enums/FulfillmentStatus.cs`
- Create: `src/Negosio.Domain/Enums/CancellationDisposition.cs`
- Delete: `src/Negosio.Domain/Enums/DeliveryStatus.cs`
- Modify: every file referencing `DeliveryStatus` / `DeliveryStatus.Delivered` (find them with a repo-wide search — at minimum `DeliveryReceipt.cs`, `DeliveryReceiptConfiguration.cs`, `DeliveryReceiptService.cs`, `DeliveryReceiptContracts.cs`, `ReportsContracts.cs`, `ReportsService.cs`, and the delivery test files)

**Interfaces:**
- Produces: `FulfillmentMethod { TakeNow = 1, Delivery = 2, Pickup = 3 }`, `FulfillmentStatus { Unscheduled = 1, Pending = 2, Completed = 3, Cancelled = 4 }`, `CancellationDisposition { DeliverLater = 1, PickupLater = 2, ConvertToDelivery = 3, ConvertToPickup = 4, CustomerPickedUpInstead = 5 }`. Every later task uses these names.

- [ ] **Step 1: Create the three enum files**

`src/Negosio.Domain/Enums/FulfillmentMethod.cs`:

```csharp
namespace Negosio.Domain.Enums;

/// <summary>
/// How a sold quantity reaches the customer. Chosen per sale line at checkout and, for the two
/// post-checkout methods, changeable afterwards only through an audited
/// <see cref="Entities.FulfillmentConversion"/>.
/// </summary>
public enum FulfillmentMethod
{
    /// <summary>The customer received the item during the original POS checkout. Completed
    /// immediately, never scheduled, and never a conversion target — it is a checkout-time method
    /// only.</summary>
    TakeNow = 1,

    /// <summary>The business transports the item to the customer.</summary>
    Delivery = 2,

    /// <summary>The customer collects the item after the original checkout.</summary>
    Pickup = 3
}
```

`src/Negosio.Domain/Enums/FulfillmentStatus.cs`:

```csharp
namespace Negosio.Domain.Enums;

/// <summary>
/// Lifecycle of fulfillment. Persisted numerically on a schedule row; values must stay stable.
/// <para><see cref="Unscheduled"/> is NEVER persisted on a schedule row — a row is always Pending,
/// Completed or Cancelled. Unscheduled describes quantity that is intended for a method but not yet
/// on any schedule, and exists here so the UI and reports share one vocabulary (the spec's label
/// table maps Delivery+Unscheduled to "Deliver later", Pickup+Unscheduled to "Pickup not
/// scheduled").</para>
/// <para>Pending → Completed and Pending → Cancelled are the only transitions; both are final. A
/// cancelled schedule is never reactivated — its quantities are released or converted, and any
/// replacement is a brand-new row.</para>
/// </summary>
public enum FulfillmentStatus
{
    Unscheduled = 1,
    Pending = 2,
    Completed = 3,
    Cancelled = 4
}
```

`src/Negosio.Domain/Enums/CancellationDisposition.cs`:

```csharp
namespace Negosio.Domain.Enums;

/// <summary>
/// What happens to a cancelled schedule's quantities. Required on every cancellation — the quantities
/// have to go somewhere, and leaving that implicit is how they get silently stranded.
/// <para>Valid pairings are enforced server-side: a Delivery may be cancelled with
/// <see cref="DeliverLater"/>, <see cref="ConvertToPickup"/> or <see cref="CustomerPickedUpInstead"/>;
/// a Pickup with <see cref="PickupLater"/> or <see cref="ConvertToDelivery"/>. There is deliberately
/// no TakeNow disposition: TakeNow means "received during checkout", which cannot become true after
/// the fact. A customer physically collecting items is
/// <see cref="CustomerPickedUpInstead"/> (which produces a Completed Pickup), never TakeNow.</para>
/// </summary>
public enum CancellationDisposition
{
    /// <summary>Delivery only. Quantities return to Delivery-unscheduled for a later delivery.</summary>
    DeliverLater = 1,

    /// <summary>Pickup only. Quantities return to Pickup-unscheduled for a later pickup.</summary>
    PickupLater = 2,

    /// <summary>Pickup only. Quantities move to Delivery intent and a new Pending Delivery is created.</summary>
    ConvertToDelivery = 3,

    /// <summary>Delivery only. Quantities move to Pickup intent and a new Pending Pickup is created.</summary>
    ConvertToPickup = 4,

    /// <summary>Delivery only. The customer already collected the items: the Delivery is cancelled and a
    /// new, immediately-Completed (Claimed) Pickup records what actually happened. The Delivery is never
    /// marked Completed — it did not happen.</summary>
    CustomerPickedUpInstead = 5
}
```

- [ ] **Step 2: Delete `DeliveryStatus.cs` and fix every reference**

Run a repo-wide search for `DeliveryStatus` and replace each usage:
- `DeliveryStatus` → `FulfillmentStatus` (the type name)
- `DeliveryStatus.Pending` → `FulfillmentStatus.Pending`
- `DeliveryStatus.Delivered` → `FulfillmentStatus.Completed`
- `DeliveryStatus.Cancelled` → `FulfillmentStatus.Cancelled`

Do **not** rename any property, DTO field, method, error code, or UI string in this task — only the enum type and its members. `DeliveryReceipt.Status`, `MarkDeliveredAsync`, `ErrorCodes.DeliveryReceiptNotPending` etc. all keep their current names here; later tasks rename what genuinely needs renaming.

The frontend has its own `DeliveryStatus` TS union (`'Pending' | 'Delivered' | 'Cancelled'`) — **leave the frontend completely untouched in this task.** The backend enum's numeric values are what persist; the JSON wire format changes from `"Delivered"` to `"Completed"`, which Task 15 handles on the frontend side. Between this task and Task 15 the frontend will show a stale label for delivered rows; that is an accepted, documented intermediate state (same compile-break-window pattern this repo's prior plan used), not a defect to fix here.

- [ ] **Step 3: Build and run the full backend suite**

Run: `dotnet build Negosio.sln`
Expected: 0 errors.

Run: `dotnet test`
Expected: every test passes. Some delivery tests assert on `DeliveryStatus.Delivered` — those are part of this task's mechanical rename and should now read `FulfillmentStatus.Completed`. If any test fails for a reason OTHER than the rename, stop: this task changes no behavior, so a behavioral failure means something else is wrong.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "refactor(delivery): replace DeliveryStatus with FulfillmentStatus/Method/Disposition enums"
```

---

## Task 2: `SaleItem` — Pickup intent, corrected Take-now, and the single audited mutator

**Files:**
- Modify: `src/Negosio.Domain/Entities/Sales/SaleItem.cs`
- Modify: `src/Negosio.Domain/Entities/Sales/Sale.cs` (`AddItem` signature only)
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/SaleItemConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Sales/SaleItemFulfillmentTests.cs` (extend the existing file)

**Interfaces:**
- Produces: `SaleItem.PickupRequiredQuantity` (`decimal`, private set), `SaleItem.TakeNowQuantity` (computed `Quantity − DeliveryRequiredQuantity − PickupRequiredQuantity`), `SaleItem.ConvertFulfillment(FulfillmentMethod from, FulfillmentMethod to, decimal quantity)`, and `Sale.AddItem(..., decimal deliveryRequiredQuantity = 0m, decimal pickupRequiredQuantity = 0m)`. Task 5 (service conversions) is the only production caller of `ConvertFulfillment`; Task 8 (checkout) is the only caller passing a non-default `pickupRequiredQuantity`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Negosio.UnitTests/Sales/SaleItemFulfillmentTests.cs`:

```csharp
    [Fact]
    public void AddItem_splits_take_now_delivery_and_pickup()
    {
        var sale = MakeSale();

        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 4m, pickupRequiredQuantity: 2m);

        item.DeliveryRequiredQuantity.Should().Be(4m);
        item.PickupRequiredQuantity.Should().Be(2m);
        item.TakeNowQuantity.Should().Be(2m);
    }

    [Fact]
    public void AddItem_rejects_allocation_exceeding_sold_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 5m, pickupRequiredQuantity: 4m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddItem_rejects_a_negative_pickup_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            pickupRequiredQuantity: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ConvertFulfillment_moves_quantity_from_delivery_to_pickup()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 6m);

        item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, 4m);

        item.DeliveryRequiredQuantity.Should().Be(2m);
        item.PickupRequiredQuantity.Should().Be(4m);
        item.TakeNowQuantity.Should().Be(2m); // unchanged — a conversion never touches take-now
    }

    [Fact]
    public void ConvertFulfillment_moves_quantity_from_pickup_to_delivery()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            pickupRequiredQuantity: 5m);

        item.ConvertFulfillment(FulfillmentMethod.Pickup, FulfillmentMethod.Delivery, 5m);

        item.PickupRequiredQuantity.Should().Be(0m);
        item.DeliveryRequiredQuantity.Should().Be(5m);
    }

    [Fact]
    public void ConvertFulfillment_rejects_more_than_the_source_method_holds()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 3m);

        var act = () => item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, 4m);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ConvertFulfillment_rejects_TakeNow_as_source_or_target()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 3m);

        var toTakeNow = () => item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.TakeNow, 1m);
        var fromTakeNow = () => item.ConvertFulfillment(FulfillmentMethod.TakeNow, FulfillmentMethod.Pickup, 1m);

        toTakeNow.Should().Throw<InvalidOperationException>();
        fromTakeNow.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ConvertFulfillment_rejects_a_non_positive_quantity()
    {
        var sale = MakeSale();
        var item = sale.AddItem(Guid.NewGuid(), "Sprite", null, "SKU", null, 65m, 8m, DiscountType.None, 0m, 520m, 0m, 0m, 520m, 30m,
            deliveryRequiredQuantity: 3m);

        var act = () => item.ConvertFulfillment(FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
```

(The file's existing `MakeSale()` helper and `using Negosio.Domain.Enums;` are already there — verify before assuming.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.UnitTests --filter SaleItemFulfillmentTests`
Expected: FAIL to compile — `AddItem` has no `pickupRequiredQuantity`, `SaleItem` has no `PickupRequiredQuantity`/`ConvertFulfillment`.

- [ ] **Step 3: Extend `SaleItem`**

Add `pickupRequiredQuantity` as the final constructor parameter (after `deliveryRequiredQuantity`), assigned alongside it. Then replace the `TakeNowQuantity` property and add the two new members:

```csharp
    /// <summary>Set once, at checkout, from the client's requested delivery quantity for this line.
    /// After checkout it changes only via <see cref="ConvertFulfillment"/>, which the calling service
    /// always pairs with an immutable FulfillmentConversion event in the same transaction — that event
    /// log is what keeps the ORIGINAL checkout allocation reconstructable (current minus replayed
    /// conversions), which is why no duplicate "original" column exists.</summary>
    public decimal DeliveryRequiredQuantity { get; private set; }

    /// <summary>The portion of this line the customer will collect after checkout. Same mutation rule as
    /// <see cref="DeliveryRequiredQuantity"/>.</summary>
    public decimal PickupRequiredQuantity { get; private set; }

    /// <summary>Computed, never persisted — the portion the customer received during checkout. Take-now
    /// is complete the moment the sale completes, and can never be scheduled or converted afterwards.</summary>
    public decimal TakeNowQuantity => Quantity - DeliveryRequiredQuantity - PickupRequiredQuantity;

    /// <summary>
    /// Moves intent quantity between the two post-checkout methods. The ONLY mutator for either intent
    /// column after checkout. Callers must write a FulfillmentConversion row in the same transaction —
    /// this method deliberately does not know about persistence, so that invariant is enforced by the
    /// service layer and its tests, not here.
    /// <para>Take-now is rejected on both sides: it means "received during checkout", which cannot become
    /// true after the fact, and its quantity is already complete so it can never be re-allocated.</para>
    /// </summary>
    public void ConvertFulfillment(FulfillmentMethod from, FulfillmentMethod to, decimal quantity)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Conversion quantity must be greater than zero.");
        }

        if (from == FulfillmentMethod.TakeNow || to == FulfillmentMethod.TakeNow)
        {
            throw new InvalidOperationException("Take-now is a checkout-time method and cannot be converted to or from.");
        }

        if (from == to)
        {
            throw new InvalidOperationException("A conversion must change the fulfillment method.");
        }

        var available = from == FulfillmentMethod.Delivery ? DeliveryRequiredQuantity : PickupRequiredQuantity;
        if (quantity > available)
        {
            throw new InvalidOperationException($"Cannot convert {quantity} — only {available} is held by {from}.");
        }

        if (from == FulfillmentMethod.Delivery)
        {
            DeliveryRequiredQuantity -= quantity;
            PickupRequiredQuantity += quantity;
        }
        else
        {
            PickupRequiredQuantity -= quantity;
            DeliveryRequiredQuantity += quantity;
        }

        Touch();
    }
```

Add `using Negosio.Domain.Enums;` if the file doesn't already have it (it does — `DiscountType` lives there).

- [ ] **Step 4: Extend `Sale.AddItem`**

Add `decimal pickupRequiredQuantity = 0m` as the new final optional parameter, pass it through to the `SaleItem` constructor, and replace the existing delivery-only guard with a combined one:

```csharp
        if (deliveryRequiredQuantity < 0m || pickupRequiredQuantity < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryRequiredQuantity), "Fulfillment quantities cannot be negative.");
        }

        if (deliveryRequiredQuantity + pickupRequiredQuantity > quantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryRequiredQuantity),
                "Delivery plus pickup quantity cannot exceed the sold quantity.");
        }
```

- [ ] **Step 5: EF configuration**

In `SaleItemConfiguration.cs`, directly after the `DeliveryRequiredQuantity` line:

```csharp
        builder.Property(i => i.PickupRequiredQuantity).HasPrecision(18, 3).HasDefaultValue(0m);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.UnitTests --filter SaleItemFulfillmentTests`
Expected: PASS (the file's pre-existing facts plus the 8 new ones).

Run: `dotnet build Negosio.sln`
Expected: 0 errors — every existing `AddItem` call site omits the new trailing optional parameter and still compiles.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(sales): SaleItem carries pickup intent and an audited conversion mutator"
```

---

## Task 3: `DeliveryReceipt` — method discriminator, generic completion, disposition, pickup factory

**Files:**
- Modify: `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs`
- Modify: `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs`
- Modify: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptSchemaTests.cs` (its direct `Create(...)` call)

**Interfaces:**
- Consumes: `FulfillmentMethod`, `FulfillmentStatus`, `CancellationDisposition` (Task 1).
- Produces: `DeliveryReceipt.Method`, `.ScheduledDate` (renamed from `ScheduledDeliveryDate`), `.CompletedAtUtc`/`.CompletedByUserId` (renamed from `DeliveredAtUtc`/`DeliveredByUserId`), `.Notes` (renamed from `DeliveryNotes`), nullable `.DeliveryAddress`, `.CancellationDisposition`; factories `CreateDelivery(...)` and `CreatePickup(...)`; `MarkDelivered(...)`, `MarkClaimed(...)`, and `Cancel(userId, reason, disposition, atUtc)`. Tasks 4-7 and the reports depend on these exact names.

- [ ] **Step 1: Write the failing tests**

In `DeliveryReceiptEntityTests.cs`, rename the existing `Make()` helper to `MakeDelivery()` and point it at the renamed factory (its existing named arguments `scheduledDeliveryDate:`/`deliveryNotes:` become `scheduledDate:`/`notes:`), then add a pickup helper and the new facts:

```csharp
    private static DeliveryReceipt MakePickup() => DeliveryReceipt.CreatePickup(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
        sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
        recipientName: "  Juan Dela Cruz ", contactNumber: " 0917 111 2222 ", notes: "  Ring the bell ",
        preparedByUserId: Guid.NewGuid(), preparedByNameSnapshot: " Cashier One ");

    [Fact]
    public void CreateDelivery_is_a_pending_delivery_with_an_address()
    {
        var dr = MakeDelivery();

        dr.Method.Should().Be(FulfillmentMethod.Delivery);
        dr.Status.Should().Be(FulfillmentStatus.Pending);
        dr.DeliveryAddress.Should().Be("123 Ayala Ave, Makati");
        dr.CancellationDisposition.Should().BeNull();
    }

    [Fact]
    public void CreatePickup_is_a_pending_pickup_with_no_address()
    {
        var pickup = MakePickup();

        pickup.Method.Should().Be(FulfillmentMethod.Pickup);
        pickup.Status.Should().Be(FulfillmentStatus.Pending);
        pickup.DeliveryAddress.Should().BeNull();
        pickup.RecipientName.Should().Be("Juan Dela Cruz");
        pickup.ContactNumber.Should().Be("0917 111 2222");
    }

    [Fact]
    public void CreateDelivery_requires_an_address()
    {
        var act = () => DeliveryReceipt.CreateDelivery(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
            sequenceNumber: 1, scheduledDate: new DateOnly(2026, 9, 15),
            recipientName: "Juan", deliveryAddress: "   ", contactNumber: null, notes: null,
            preparedByUserId: Guid.NewGuid(), preparedByNameSnapshot: "x");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarkClaimed_completes_a_pending_pickup_and_stamps_audit_fields()
    {
        var pickup = MakePickup();
        var by = Guid.NewGuid();
        var at = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

        pickup.MarkClaimed(by, at);

        pickup.Status.Should().Be(FulfillmentStatus.Completed);
        pickup.CompletedAtUtc.Should().Be(at);
        pickup.CompletedByUserId.Should().Be(by);
    }

    [Fact]
    public void MarkClaimed_on_a_delivery_throws()
    {
        var dr = MakeDelivery();

        var act = () => dr.MarkClaimed(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_on_a_pickup_throws()
    {
        var pickup = MakePickup();

        var act = () => pickup.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_records_its_disposition()
    {
        var dr = MakeDelivery();

        dr.Cancel(Guid.NewGuid(), "Customer rescheduled", CancellationDisposition.DeliverLater, DateTime.UtcNow);

        dr.Status.Should().Be(FulfillmentStatus.Cancelled);
        dr.CancellationDisposition.Should().Be(CancellationDisposition.DeliverLater);
    }

    [Fact]
    public void Cancel_rejects_a_disposition_that_does_not_match_the_method()
    {
        var dr = MakeDelivery();
        var pickup = MakePickup();

        var deliveryWithPickupDisposition =
            () => dr.Cancel(Guid.NewGuid(), "x", CancellationDisposition.PickupLater, DateTime.UtcNow);
        var pickupWithDeliveryDisposition =
            () => pickup.Cancel(Guid.NewGuid(), "x", CancellationDisposition.DeliverLater, DateTime.UtcNow);
        var pickupPickedUpInstead =
            () => pickup.Cancel(Guid.NewGuid(), "x", CancellationDisposition.CustomerPickedUpInstead, DateTime.UtcNow);

        deliveryWithPickupDisposition.Should().Throw<InvalidOperationException>();
        pickupWithDeliveryDisposition.Should().Throw<InvalidOperationException>();
        pickupPickedUpInstead.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkClaimed_twice_throws()
    {
        var pickup = MakePickup();
        pickup.MarkClaimed(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => pickup.MarkClaimed(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
```

Every existing fact in the file that calls `Make()`, `dr.Cancel(x, reason, at)` (3 args), or asserts on `ScheduledDeliveryDate`/`DeliveredAtUtc`/`DeliveryNotes` needs updating to the new names/arities — read the whole file and update each one; do not leave a stale call.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Negosio.UnitTests --filter DeliveryReceiptEntityTests`
Expected: FAIL to compile.

- [ ] **Step 3: Rewrite `DeliveryReceipt.cs`**

```csharp
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// One scheduled (or completed, or cancelled) fulfillment against a <see cref="Sale"/> — a delivery the
/// business makes, or a pickup the customer collects. <see cref="Method"/> discriminates the two; the
/// table keeps its historical `DeliveryReceipts` name because renaming it would be pure migration risk
/// for no functional gain.
/// <para>A sale may have several of these per method. <see cref="SequenceNumber"/> is a per-sale,
/// per-method label ("Delivery 1", "Pickup 1"), never a global document number. A cancelled record is
/// kept forever as audit history — rescheduling or converting always creates a brand-new row and never
/// reactivates this one.</para>
/// </summary>
public class DeliveryReceipt : Entity
{
    private readonly List<DeliveryReceiptItem> _items = new();

    private DeliveryReceipt()
    {
        RecipientName = string.Empty;
        PreparedByNameSnapshot = string.Empty;
    }

    private DeliveryReceipt(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        FulfillmentMethod method,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string? deliveryAddress,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        SaleId = saleId;
        RelatedSaleNumber = relatedSaleNumber;
        Method = method;
        SequenceNumber = sequenceNumber;
        ScheduledDate = scheduledDate;
        RecipientName = recipientName;
        DeliveryAddress = deliveryAddress;
        ContactNumber = contactNumber;
        Notes = notes;
        PreparedByUserId = preparedByUserId;
        PreparedByNameSnapshot = preparedByNameSnapshot;
        BatchRequestId = batchRequestId;
        Status = FulfillmentStatus.Pending;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? SaleId { get; private set; }

    public string? RelatedSaleNumber { get; private set; }

    /// <summary>Delivery or Pickup. Never TakeNow — take-now quantity is complete at checkout and is
    /// never scheduled.</summary>
    public FulfillmentMethod Method { get; private set; }

    /// <summary>Per-sale, per-method label ("Delivery 1", "Pickup 1") — never a global number.</summary>
    public int SequenceNumber { get; private set; }

    /// <summary>The delivery date for a Delivery, the expected pickup date for a Pickup.</summary>
    public DateOnly ScheduledDate { get; private set; }

    /// <summary>Always Pending, Completed or Cancelled — never Unscheduled, which describes quantity
    /// not on any schedule rather than a row state.</summary>
    public FulfillmentStatus Status { get; private set; }

    /// <summary>Recipient for a Delivery, collecting customer for a Pickup.</summary>
    public string RecipientName { get; private set; }

    /// <summary>Required for Delivery, always null for Pickup (nothing is transported).</summary>
    public string? DeliveryAddress { get; private set; }

    public string? ContactNumber { get; private set; }

    public string? Notes { get; private set; }

    public Guid PreparedByUserId { get; private set; }

    public string PreparedByNameSnapshot { get; private set; }

    /// <summary>When the fulfillment actually succeeded — delivered for a Delivery, claimed for a
    /// Pickup. One pair of columns for both, labelled per method in the UI.</summary>
    public DateTime? CompletedAtUtc { get; private set; }

    public Guid? CompletedByUserId { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Where this row's quantities went when it was cancelled. Null unless
    /// <see cref="Status"/> is Cancelled.</summary>
    public CancellationDisposition? CancellationDisposition { get; private set; }

    /// <summary>Client-supplied idempotency key for the batch-create call that produced this row (and
    /// every sibling in the same batch) — null for a schedule created on its own.</summary>
    public Guid? BatchRequestId { get; private set; }

    /// <summary>SQL Server `rowversion` — EF-managed optimistic concurrency token, never set by
    /// application code. Protects two competing status changes on the same schedule.</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<DeliveryReceiptItem> Items => _items.AsReadOnly();

    public static DeliveryReceipt CreateDelivery(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId = null)
    {
        var trimmedAddress = deliveryAddress?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedAddress))
        {
            throw new ArgumentException("Delivery address is required.", nameof(deliveryAddress));
        }

        return Create(
            tenantId, branchId, saleId, relatedSaleNumber, FulfillmentMethod.Delivery, sequenceNumber,
            scheduledDate, recipientName, trimmedAddress, contactNumber, notes, preparedByUserId,
            preparedByNameSnapshot, batchRequestId);
    }

    public static DeliveryReceipt CreatePickup(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId = null) =>
        Create(
            tenantId, branchId, saleId, relatedSaleNumber, FulfillmentMethod.Pickup, sequenceNumber,
            scheduledDate, recipientName, deliveryAddress: null, contactNumber, notes, preparedByUserId,
            preparedByNameSnapshot, batchRequestId);

    private static DeliveryReceipt Create(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        FulfillmentMethod method,
        int sequenceNumber,
        DateOnly scheduledDate,
        string recipientName,
        string? deliveryAddress,
        string? contactNumber,
        string? notes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number must be at least 1.");
        }

        var trimmedRecipientName = recipientName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmedRecipientName))
        {
            throw new ArgumentException("Recipient name is required.", nameof(recipientName));
        }

        return new DeliveryReceipt(
            tenantId,
            branchId,
            saleId,
            NullIfBlank(relatedSaleNumber),
            method,
            sequenceNumber,
            scheduledDate,
            trimmedRecipientName,
            NullIfBlank(deliveryAddress),
            NullIfBlank(contactNumber),
            NullIfBlank(notes),
            preparedByUserId,
            preparedByNameSnapshot?.Trim() ?? string.Empty,
            batchRequestId);
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    public DeliveryReceiptItem AddItem(
        Guid saleItemId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        decimal quantity,
        decimal? unitPrice)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        var item = new DeliveryReceiptItem(
            TenantId, Id, saleItemId, productNameSnapshot, variantNameSnapshot, quantity, unitPrice);
        _items.Add(item);
        return item;
    }

    /// <summary>Completes a Delivery — the business delivered it. <paramref name="completedAtUtc"/> is
    /// supplied by the caller (one TimeProvider-sourced timestamp per attempt), matching
    /// <see cref="Sale.Void"/>'s established pattern.</summary>
    public void MarkDelivered(Guid completedByUserId, DateTime completedAtUtc)
    {
        if (Method != FulfillmentMethod.Delivery)
        {
            throw new InvalidOperationException("Only a delivery can be marked delivered. Use MarkClaimed for a pickup.");
        }

        Complete(completedByUserId, completedAtUtc, "Only a pending delivery can be marked delivered.");
    }

    /// <summary>Completes a Pickup — the customer collected it. This is the ONLY successful outcome for a
    /// pickup: never cancel a pickup the customer actually collected, and never record it as take-now.</summary>
    public void MarkClaimed(Guid completedByUserId, DateTime completedAtUtc)
    {
        if (Method != FulfillmentMethod.Pickup)
        {
            throw new InvalidOperationException("Only a pickup can be marked claimed. Use MarkDelivered for a delivery.");
        }

        Complete(completedByUserId, completedAtUtc, "Only a pending pickup can be marked claimed.");
    }

    private void Complete(Guid completedByUserId, DateTime completedAtUtc, string wrongStateMessage)
    {
        if (Status != FulfillmentStatus.Pending)
        {
            throw new InvalidOperationException(wrongStateMessage);
        }

        Status = FulfillmentStatus.Completed;
        CompletedAtUtc = completedAtUtc;
        CompletedByUserId = completedByUserId;
        Touch();
    }

    /// <summary>
    /// Cancels a pending schedule, recording where its quantities went. The row and its items are kept
    /// forever as history — quantities are "released" only in the sense that availability computation
    /// ignores cancelled rows. Any replacement schedule is a separate, brand-new row.
    /// <para><paramref name="disposition"/> must be valid for this row's <see cref="Method"/>; take-now
    /// is never a valid disposition.</para>
    /// </summary>
    public void Cancel(Guid cancelledByUserId, string reason, CancellationDisposition disposition, DateTime cancelledAtUtc)
    {
        if (Status != FulfillmentStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending schedule can be cancelled.");
        }

        var allowed = Method switch
        {
            FulfillmentMethod.Delivery =>
                disposition is Enums.CancellationDisposition.DeliverLater
                    or Enums.CancellationDisposition.ConvertToPickup
                    or Enums.CancellationDisposition.CustomerPickedUpInstead,
            FulfillmentMethod.Pickup =>
                disposition is Enums.CancellationDisposition.PickupLater
                    or Enums.CancellationDisposition.ConvertToDelivery,
            _ => false,
        };

        if (!allowed)
        {
            throw new InvalidOperationException($"{disposition} is not a valid disposition for a {Method} schedule.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        Status = FulfillmentStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
        CancelledByUserId = cancelledByUserId;
        CancellationReason = trimmedReason;
        CancellationDisposition = disposition;
        Touch();
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Negosio.UnitTests --filter DeliveryReceiptEntityTests`
Expected: PASS.

Run: `dotnet build Negosio.sln`
Expected: FAILS — `DeliveryReceiptService.cs`, `ReportsService.cs`, and their DTO/mapping code still use `Create(...)`, `ScheduledDeliveryDate`, `DeliveredAtUtc`, `DeliveryNotes`, and the 3-arg `Cancel`. That is this task's intentional handoff; Tasks 4-7 fix them. Confirm the failures are confined to the delivery/report application layer and its tests — anything outside that is an unexpected regression worth stopping for.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(delivery): DeliveryReceipt becomes a method-discriminated fulfillment schedule"
```

---

## Task 4: `FulfillmentConversion` audit event + EF configuration for everything new

**Files:**
- Create: `src/Negosio.Domain/Entities/Delivery/FulfillmentConversion.cs`
- Create: `src/Negosio.Infrastructure/Persistence/Configurations/FulfillmentConversionConfiguration.cs`
- Modify: `src/Negosio.Application/Abstractions/ITenantDbContext.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs`
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/DeliveryReceiptConfiguration.cs`
- Test: `tests/Negosio.UnitTests/Delivery/FulfillmentConversionTests.cs` (new)

**Interfaces:**
- Produces: `FulfillmentConversion.Record(...)` factory and its properties; `ITenantDbContext.FulfillmentConversions`. Task 7 (service conversions) writes these rows; Task 11 (combined report) reads them.

- [ ] **Step 1: Write the failing test**

`tests/Negosio.UnitTests/Delivery/FulfillmentConversionTests.cs`:

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Delivery;

public class FulfillmentConversionTests
{
    private static FulfillmentConversion Make(
        FulfillmentMethod from = FulfillmentMethod.Delivery,
        FulfillmentMethod to = FulfillmentMethod.Pickup,
        decimal quantity = 2m) =>
        FulfillmentConversion.Record(
            tenantId: Guid.NewGuid(), saleId: Guid.NewGuid(), saleItemId: Guid.NewGuid(),
            quantity: quantity, fromMethod: from, toMethod: to,
            sourceRecordId: Guid.NewGuid(), replacementRecordId: Guid.NewGuid(),
            reason: "  Customer picked up instead  ", createdByUserId: Guid.NewGuid(),
            createdAtUtc: new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void Record_captures_the_conversion()
    {
        var e = Make();

        e.FromMethod.Should().Be(FulfillmentMethod.Delivery);
        e.ToMethod.Should().Be(FulfillmentMethod.Pickup);
        e.Quantity.Should().Be(2m);
        e.Reason.Should().Be("Customer picked up instead");
        e.SourceRecordId.Should().NotBeNull();
        e.ReplacementRecordId.Should().NotBeNull();
    }

    [Fact]
    public void Record_allows_a_null_replacement_for_a_release_back_to_unscheduled()
    {
        var e = FulfillmentConversion.Record(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3m,
            FulfillmentMethod.Delivery, FulfillmentMethod.Delivery,
            sourceRecordId: Guid.NewGuid(), replacementRecordId: null,
            reason: "Deliver later", createdByUserId: Guid.NewGuid(), createdAtUtc: DateTime.UtcNow);

        e.ReplacementRecordId.Should().BeNull();
        e.FromMethod.Should().Be(e.ToMethod); // a release is a same-method event, recorded for the audit trail
    }

    [Fact]
    public void Record_rejects_a_non_positive_quantity()
    {
        var act = () => Make(quantity: 0m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Record_rejects_TakeNow_on_either_side()
    {
        var fromTakeNow = () => Make(from: FulfillmentMethod.TakeNow);
        var toTakeNow = () => Make(to: FulfillmentMethod.TakeNow);

        fromTakeNow.Should().Throw<InvalidOperationException>();
        toTakeNow.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Record_requires_a_reason()
    {
        var act = () => FulfillmentConversion.Record(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1m,
            FulfillmentMethod.Delivery, FulfillmentMethod.Pickup, Guid.NewGuid(), Guid.NewGuid(),
            reason: "   ", createdByUserId: Guid.NewGuid(), createdAtUtc: DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Negosio.UnitTests --filter FulfillmentConversionTests`
Expected: FAIL to compile — the type doesn't exist.

- [ ] **Step 3: Create the entity**

`src/Negosio.Domain/Entities/Delivery/FulfillmentConversion.cs`:

```csharp
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// An immutable record of one post-sale fulfillment change for one sale line: quantity moving between
/// Delivery and Pickup intent, or being released back to unscheduled. Written in the same transaction as
/// the <see cref="SaleItem.ConvertFulfillment"/> call (or the cancellation) that caused it.
/// <para>This log is what makes the ORIGINAL checkout allocation reconstructable — replaying these
/// events backwards over the current intent columns yields what the cashier chose at checkout — which is
/// why <see cref="SaleItem"/> carries no duplicate "original" columns.</para>
/// <para>Never updated, never deleted. There is no mutator on this type by design.</para>
/// </summary>
public class FulfillmentConversion : Entity
{
    private FulfillmentConversion()
    {
        Reason = string.Empty;
    }

    private FulfillmentConversion(
        Guid tenantId,
        Guid saleId,
        Guid saleItemId,
        decimal quantity,
        FulfillmentMethod fromMethod,
        FulfillmentMethod toMethod,
        Guid? sourceRecordId,
        Guid? replacementRecordId,
        string reason,
        Guid createdByUserId,
        DateTime createdAtUtc)
    {
        TenantId = tenantId;
        SaleId = saleId;
        SaleItemId = saleItemId;
        Quantity = quantity;
        FromMethod = fromMethod;
        ToMethod = toMethod;
        SourceRecordId = sourceRecordId;
        ReplacementRecordId = replacementRecordId;
        Reason = reason;
        CreatedByUserId = createdByUserId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid TenantId { get; private set; }

    public Guid SaleId { get; private set; }

    public Guid SaleItemId { get; private set; }

    public decimal Quantity { get; private set; }

    public FulfillmentMethod FromMethod { get; private set; }

    /// <summary>Equal to <see cref="FromMethod"/> when the event records a release back to the same
    /// method's unscheduled pool rather than a cross-method move.</summary>
    public FulfillmentMethod ToMethod { get; private set; }

    /// <summary>The schedule this quantity came off — the cancelled row.</summary>
    public Guid? SourceRecordId { get; private set; }

    /// <summary>The schedule this quantity went onto, when the disposition created one immediately. Null
    /// for a plain release back to unscheduled.</summary>
    public Guid? ReplacementRecordId { get; private set; }

    public string Reason { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static FulfillmentConversion Record(
        Guid tenantId,
        Guid saleId,
        Guid saleItemId,
        decimal quantity,
        FulfillmentMethod fromMethod,
        FulfillmentMethod toMethod,
        Guid? sourceRecordId,
        Guid? replacementRecordId,
        string reason,
        Guid createdByUserId,
        DateTime createdAtUtc)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Conversion quantity must be greater than zero.");
        }

        if (fromMethod == FulfillmentMethod.TakeNow || toMethod == FulfillmentMethod.TakeNow)
        {
            throw new InvalidOperationException(
                "Take-now is a checkout-time method and can never be a conversion source or target.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
        {
            throw new ArgumentException("A conversion reason is required.", nameof(reason));
        }

        return new FulfillmentConversion(
            tenantId, saleId, saleItemId, quantity, fromMethod, toMethod,
            sourceRecordId, replacementRecordId, trimmedReason, createdByUserId, createdAtUtc);
    }
}
```

If `Entity`'s `CreatedAtUtc`/`UpdatedAtUtc` have private setters that the base class assigns itself, drop those two assignments from the constructor and let the base handle them — read `src/Negosio.Domain/Common/Entity.cs` first and match whatever it actually does rather than fighting it.

- [ ] **Step 4: Register the DbSet**

In `src/Negosio.Application/Abstractions/ITenantDbContext.cs`, beside the existing `DbSet<DeliveryReceipt> DeliveryReceipts { get; }`:

```csharp
    DbSet<FulfillmentConversion> FulfillmentConversions { get; }
```

In `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs`, beside the existing `DeliveryReceipts` property:

```csharp
    public DbSet<FulfillmentConversion> FulfillmentConversions => Set<FulfillmentConversion>();
```

- [ ] **Step 5: EF configuration for the conversion log**

`src/Negosio.Infrastructure/Persistence/Configurations/FulfillmentConversionConfiguration.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class FulfillmentConversionConfiguration : IEntityTypeConfiguration<FulfillmentConversion>
{
    public void Configure(EntityTypeBuilder<FulfillmentConversion> b)
    {
        b.ToTable("FulfillmentConversions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.SaleId).IsRequired();
        b.Property(x => x.SaleItemId).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.FromMethod).IsRequired().HasConversion<int>();
        b.Property(x => x.ToMethod).IsRequired().HasConversion<int>();
        b.Property(x => x.SourceRecordId);
        b.Property(x => x.ReplacementRecordId);
        b.Property(x => x.Reason).IsRequired().HasMaxLength(500);
        b.Property(x => x.CreatedByUserId).IsRequired();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SaleItem>().WithMany().HasForeignKey(x => x.SaleItemId).OnDelete(DeleteBehavior.Restrict);

        // Sale-detail's conversion history and the combined report both read by sale, newest first.
        b.HasIndex(x => new { x.TenantId, x.SaleId, x.CreatedAtUtc })
            .HasDatabaseName("IX_FulfillmentConversions_TenantId_SaleId_CreatedAtUtc");
        // Per-line audit replay (reconstructing the original checkout allocation).
        b.HasIndex(x => new { x.TenantId, x.SaleItemId })
            .HasDatabaseName("IX_FulfillmentConversions_TenantId_SaleItemId");
        // Retry-safety: one conversion row per (source schedule, sale item) — a retried cancellation
        // that re-runs the same conversion collides here instead of double-counting the quantity.
        // Filtered because a release with no source record leaves SourceRecordId null.
        b.HasIndex(x => new { x.SourceRecordId, x.SaleItemId })
            .IsUnique()
            .HasFilter("[SourceRecordId] IS NOT NULL")
            .HasDatabaseName("IX_FulfillmentConversions_SourceRecordId_SaleItemId");
    }
}
```

- [ ] **Step 6: EF configuration for the discriminated schedule**

In `DeliveryReceiptConfiguration.cs`, inside `DeliveryReceiptConfiguration.Configure`:

Rename the three renamed property mappings (`ScheduledDeliveryDate` → `ScheduledDate`, `DeliveredAtUtc`/`DeliveredByUserId` → `CompletedAtUtc`/`CompletedByUserId`, `DeliveryNotes` → `Notes`), make the address nullable, and add the method + disposition:

```csharp
        // Existing rows are all deliveries; the default is what the migration backfills them with.
        b.Property(x => x.Method).IsRequired().HasConversion<int>().HasDefaultValue(FulfillmentMethod.Delivery);
        b.Property(x => x.ScheduledDate).IsRequired();
        // Nullable now: a pickup transports nothing, so it has no address. Required-for-delivery is a
        // domain rule (DeliveryReceipt.CreateDelivery), deliberately not a schema rule — the column has to
        // permit null for pickup rows.
        b.Property(x => x.DeliveryAddress).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CompletedAtUtc);
        b.Property(x => x.CompletedByUserId);
        b.Property(x => x.CancellationDisposition).HasConversion<int>();
```

Replace the `(SaleId, SequenceNumber)` unique index with a method-scoped one — "Delivery 1" and "Pickup 1" must be able to coexist on one sale:

```csharp
        // Per-sale, per-method label. SaleId is nullable and EF's SQL Server provider adds
        // `WHERE [SaleId] IS NOT NULL` for a nullable unique index, so a sale-less schedule never
        // collides here.
        b.HasIndex(x => new { x.SaleId, x.Method, x.SequenceNumber })
            .IsUnique().HasDatabaseName("IX_DeliveryReceipts_SaleId_Method_SequenceNumber");
```

And add a method-aware report index beside the existing ones:

```csharp
        b.HasIndex(x => new { x.TenantId, x.Method, x.Status, x.ScheduledDate })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_Method_Status_ScheduledDate");
```

Delete the now-superseded `IX_DeliveryReceipts_SaleId_SequenceNumber` and `IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate` index definitions.

- [ ] **Step 7: Run the unit tests and build the infrastructure project**

Run: `dotnet test tests/Negosio.UnitTests --filter FulfillmentConversionTests`
Expected: PASS.

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors. (The solution as a whole still fails on the application layer — Task 3's intentional handoff, still open until Task 7.)

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(delivery): immutable FulfillmentConversion log and EF config for the discriminated schedule"
```

---

## Task 5: Contracts — pickup, dispositions, conversions, the fulfillment summary

Types-only. This is the coordination point every remaining backend task and the whole frontend depends on — get the names exactly right. It is **expected** to leave `DeliveryReceiptService.cs`, `SalesController.cs`, `DeliveryReceiptsController.cs` and `ReportsService.cs` non-compiling; Task 6 and Tasks 8-11 close that. Do not touch those files here.

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs` (full replacement)
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptValidators.cs` (full replacement)
- Modify: `src/Negosio.Application/Delivery/SaleFulfillmentCalculator.cs` (full replacement)
- Modify: `src/Negosio.Application/Common/ErrorCodes.cs`
- Modify: `src/Negosio.Api/Authorization/AuthorizationPolicies.cs`

**Interfaces:** produces every DTO, the `IDeliveryReceiptService` interface, the new error codes, and the `FulfillmentCancel` policy used by Tasks 6, 8-11 and 14-19.

- [ ] **Step 1: Replace `DeliveryReceiptContracts.cs`**

Write these types exactly. Field names are the wire contract — the frontend mirrors them in camelCase.

```csharp
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

// ---- Shared line input ----

public sealed record FulfillmentItemInput(Guid SaleItemId, decimal Quantity);

// ---- Create (delivery) ----

/// <summary>One delivery schedule. Used standalone and as an entry of a checkout batch.</summary>
public sealed record CreateDeliveryReceiptRequest(
    DateOnly ScheduledDate,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? Notes,
    IReadOnlyList<FulfillmentItemInput> Items);

public sealed record CreateDeliveryReceiptBatchRequest(
    Guid BatchRequestId,
    IReadOnlyList<CreateDeliveryReceiptRequest> Schedules);

// ---- Create (pickup) ----

/// <summary>One pickup schedule. No address — a pickup transports nothing.</summary>
public sealed record CreatePickupRequest(
    DateOnly ScheduledDate,
    string RecipientName,
    string? ContactNumber,
    string? Notes,
    IReadOnlyList<FulfillmentItemInput> Items);

public sealed record CreatePickupBatchRequest(
    Guid BatchRequestId,
    IReadOnlyList<CreatePickupRequest> Schedules);

public sealed record FulfillmentBatchResultDto(
    IReadOnlyList<FulfillmentScheduleDto> Created,
    bool WasExistingBatch);

// ---- Cancellation with disposition ----

/// <summary>
/// Cancel a pending DELIVERY. <see cref="Disposition"/> must be DeliverLater, ConvertToPickup or
/// CustomerPickedUpInstead. <see cref="Replacement"/> is required for ConvertToPickup (the future pickup
/// to create) and for CustomerPickedUpInstead (the already-collected pickup to record, which is created
/// directly as Completed/Claimed); it must be null for DeliverLater.
/// </summary>
public sealed record CancelDeliveryRequest(
    string Reason,
    CancellationDisposition Disposition,
    PickupReplacementInput? Replacement);

/// <summary>
/// Cancel a pending PICKUP. <see cref="Disposition"/> must be PickupLater or ConvertToDelivery.
/// <see cref="Replacement"/> is required for ConvertToDelivery, null for PickupLater.
/// </summary>
public sealed record CancelPickupRequest(
    string Reason,
    CancellationDisposition Disposition,
    DeliveryReplacementInput? Replacement);

/// <summary>The pickup to create when a delivery is cancelled into one. For
/// CustomerPickedUpInstead the date is the collection date (today or earlier is fine — it already
/// happened), and the created record is Completed immediately.</summary>
public sealed record PickupReplacementInput(
    DateOnly ScheduledDate,
    string RecipientName,
    string? ContactNumber,
    string? Notes);

public sealed record DeliveryReplacementInput(
    DateOnly ScheduledDate,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? Notes);

/// <summary>A cancellation that creates a replacement returns both records, so the caller can show what
/// happened without a second round trip.</summary>
public sealed record CancellationResultDto(
    FulfillmentScheduleDto Cancelled,
    FulfillmentScheduleDto? Replacement);

// ---- Read ----

public sealed record FulfillmentItemDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount);

/// <summary>One delivery or pickup schedule, fully rendered (print-ready).</summary>
public sealed record FulfillmentScheduleDto(
    Guid Id,
    Guid? SaleId,
    string? RelatedSaleNumber,
    FulfillmentMethod Method,
    /// <summary>Per-sale, per-method label: "Delivery 1", "Pickup 1".</summary>
    int SequenceNumber,
    DateOnly ScheduledDate,
    FulfillmentStatus Status,
    DateTime CreatedAtUtc,
    string BranchName,
    string RecipientName,
    string? DeliveryAddress,
    string? ContactNumber,
    string? Notes,
    string PreparedByName,
    DateTime? CompletedAtUtc,
    string? CompletedByName,
    DateTime? CancelledAtUtc,
    string? CancelledByName,
    string? CancellationReason,
    CancellationDisposition? CancellationDisposition,
    IReadOnlyList<FulfillmentItemDto> Items,
    /// <summary>Read live from the linked Sale, never stored here. Always 0 for a pickup — a pickup
    /// never carries a delivery charge.</summary>
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

// ---- Sale-level fulfillment ----

/// <summary>
/// REPLACES the shipped 7-member enum entirely. The old members (Unscheduled, PartiallyScheduled,
/// FullyScheduled, PartiallyDelivered, FullyDelivered, NeedsRescheduling) described a delivery-only
/// world and cannot express "awaiting pickup". The spec names the replacement set directly, so this
/// is a rename of the concept, not an extension of it. Consumers to update: SaleDetailPage,
/// lib/pos.ts's SALE_FULFILLMENT_STATUS_LABELS, and the delivery report's status column.
/// Note that DeliveryReportPreset's "NeedsRescheduling" is a REPORT FILTER, a different type —
/// leave it alone.
/// </summary>
public enum SaleFulfillmentStatus
{
    /// <summary>Nothing on this sale was marked for delivery or pickup — everything was taken at the
    /// counter, so there is nothing to track.</summary>
    NotApplicable = 1,
    /// <summary>TakenNow + Delivered + Claimed == sold quantity.</summary>
    Fulfilled = 2,
    PartiallyFulfilled = 3,
    AwaitingDelivery = 4,
    AwaitingPickup = 5,
    AwaitingDeliveryAndPickup = 6,
    /// <summary>Intent exists but nothing is scheduled yet.</summary>
    NeedsScheduling = 7,
    /// <summary>Something is overdue — a pending schedule whose date has passed.</summary>
    NeedsAttention = 8,
}

/// <summary>The 8-bucket per-line breakdown the Sale-detail page renders. Every bucket is derived
/// server-side; the frontend never recomputes one.</summary>
public sealed record SaleItemFulfillmentDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal TakeNowQuantity,
    decimal DeliveryUnscheduledQuantity,
    decimal DeliveryPendingQuantity,
    decimal DeliveredQuantity,
    decimal PickupUnscheduledQuantity,
    decimal PickupPendingQuantity,
    decimal ClaimedQuantity);

public sealed record FulfillmentConversionDto(
    Guid Id,
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    FulfillmentMethod FromMethod,
    FulfillmentMethod ToMethod,
    Guid? SourceRecordId,
    Guid? ReplacementRecordId,
    string Reason,
    DateTime CreatedAtUtc,
    string CreatedByName);

public sealed record SaleFulfillmentSummaryDto(
    Guid SaleId,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    IReadOnlyList<SaleItemFulfillmentDto> Items,
    IReadOnlyList<FulfillmentScheduleDto> Deliveries,
    IReadOnlyList<FulfillmentScheduleDto> Pickups,
    IReadOnlyList<FulfillmentConversionDto> Conversions,
    /// <summary>True when any line still has delivery-unscheduled quantity. Gates "Create delivery";
    /// when false the UI shows "All delivery items have already been scheduled or delivered."</summary>
    bool CanCreateDelivery,
    /// <summary>True when any line still has pickup-unscheduled quantity. Gates "Create pickup"; when
    /// false the UI shows "All pickup items have already been scheduled or claimed."</summary>
    bool CanCreatePickup);

public interface IDeliveryReceiptService
{
    Task<FulfillmentBatchResultDto> CreateDeliveryBatchAsync(Guid saleId, CreateDeliveryReceiptBatchRequest request, CancellationToken ct = default);
    Task<FulfillmentScheduleDto> CreateDeliveryAsync(Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<FulfillmentBatchResultDto> CreatePickupBatchAsync(Guid saleId, CreatePickupBatchRequest request, CancellationToken ct = default);
    Task<FulfillmentScheduleDto> CreatePickupAsync(Guid saleId, CreatePickupRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<FulfillmentScheduleDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default);
    Task<FulfillmentScheduleDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<SaleFulfillmentSummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default);

    Task<FulfillmentScheduleDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default);
    Task<FulfillmentScheduleDto> MarkClaimedAsync(Guid id, CancellationToken ct = default);

    Task<CancellationResultDto> CancelDeliveryAsync(Guid id, CancelDeliveryRequest request, CancellationToken ct = default);
    Task<CancellationResultDto> CancelPickupAsync(Guid id, CancelPickupRequest request, CancellationToken ct = default);
}
```

- [ ] **Step 2: Replace `DeliveryReceiptValidators.cs`**

One validator per request type. Rules, written as FluentValidation `AbstractValidator<T>` classes following the file's existing style:

- `FulfillmentItemInputValidator`: `SaleItemId` not empty; `Quantity` > 0.
- `CreateDeliveryReceiptRequestValidator`: `RecipientName` not empty, max 120; `DeliveryAddress` not empty, max 300; `ContactNumber` max 40 when not null; `Notes` max 1000 when not null; `Items` not empty, each via `FulfillmentItemInputValidator`, and `SaleItemId` distinct across the list (message: "Each sale item may be listed at most once per schedule."); `ScheduledDate` not `default(DateOnly)`.
- `CreatePickupRequestValidator`: identical **minus** the `DeliveryAddress` rule.
- `CreateDeliveryReceiptBatchRequestValidator` / `CreatePickupBatchRequestValidator`: `BatchRequestId` not empty; `Schedules` not empty; each child via the matching validator.
- `PickupReplacementInputValidator`: `RecipientName` not empty, max 120; `ContactNumber` max 40; `Notes` max 1000; `ScheduledDate` not default.
- `DeliveryReplacementInputValidator`: same plus `DeliveryAddress` not empty, max 300.
- `CancelDeliveryRequestValidator`: `Reason` not empty, max 500; `Disposition` must be `DeliverLater`, `ConvertToPickup` or `CustomerPickedUpInstead` (`.Must(...)`, message "That disposition is not valid for a delivery."); `Replacement` must be non-null when `Disposition` is `ConvertToPickup` or `CustomerPickedUpInstead` and null when it is `DeliverLater`; when non-null, validated by `PickupReplacementInputValidator`.
- `CancelPickupRequestValidator`: `Reason` not empty, max 500; `Disposition` must be `PickupLater` or `ConvertToDelivery`; `Replacement` non-null exactly when `ConvertToDelivery`; when non-null, validated by `DeliveryReplacementInputValidator`.

The `Disposition` whitelist here is a fast, friendly 400 — the domain's `DeliveryReceipt.Cancel` guard (Task 3) remains the authoritative backstop, and the service re-checks too. Three layers is intentional for the one rule the spec is most emphatic about (no TakeNow disposition, no cross-method disposition).

- [ ] **Step 3: Replace `SaleFulfillmentCalculator.cs`**

```csharp
namespace Negosio.Application.Delivery;

/// <summary>
/// Derives a sale's overall <see cref="SaleFulfillmentStatus"/> from its aggregate quantities.
/// Priority-ordered — first matching rule wins. Pure and stateless, and deliberately shared by the
/// Sale-detail summary and the combined report so the two views can never disagree.
/// </summary>
public static class SaleFulfillmentCalculator
{
    public static SaleFulfillmentStatus Derive(
        decimal soldQuantity,
        decimal takeNowQuantity,
        decimal deliveredQuantity,
        decimal claimedQuantity,
        decimal deliveryPendingQuantity,
        decimal pickupPendingQuantity,
        decimal deliveryUnscheduledQuantity,
        decimal pickupUnscheduledQuantity,
        bool hasOverduePendingSchedule)
    {
        var trackedQuantity = soldQuantity - takeNowQuantity;
        if (trackedQuantity <= 0m) return SaleFulfillmentStatus.NotApplicable;

        if (takeNowQuantity + deliveredQuantity + claimedQuantity >= soldQuantity)
        {
            return SaleFulfillmentStatus.Fulfilled;
        }

        if (hasOverduePendingSchedule) return SaleFulfillmentStatus.NeedsAttention;

        var awaitingDelivery = deliveryPendingQuantity > 0m;
        var awaitingPickup = pickupPendingQuantity > 0m;
        if (awaitingDelivery && awaitingPickup) return SaleFulfillmentStatus.AwaitingDeliveryAndPickup;
        if (awaitingDelivery) return SaleFulfillmentStatus.AwaitingDelivery;
        if (awaitingPickup) return SaleFulfillmentStatus.AwaitingPickup;

        // Nothing pending. Either some quantity has completed (partially fulfilled with the rest
        // unscheduled), or nothing has completed at all (nothing scheduled yet).
        if (deliveryUnscheduledQuantity > 0m || pickupUnscheduledQuantity > 0m)
        {
            return deliveredQuantity + claimedQuantity > 0m
                ? SaleFulfillmentStatus.PartiallyFulfilled
                : SaleFulfillmentStatus.NeedsScheduling;
        }

        return SaleFulfillmentStatus.PartiallyFulfilled;
    }
}
```

- [ ] **Step 4: New error codes**

Append these five to the `// ---- Delivery receipts ----` block in `ErrorCodes.cs`:

```csharp
    public const string FulfillmentMethodMismatch = "FULFILLMENT_METHOD_MISMATCH";
    public const string InvalidCancellationDisposition = "INVALID_CANCELLATION_DISPOSITION";
    public const string ReplacementDetailsRequired = "REPLACEMENT_DETAILS_REQUIRED";
    public const string PickupQuantityExceedsAvailable = "PICKUP_QUANTITY_EXCEEDS_AVAILABLE";
    public const string PickupScheduleDateInPast = "PICKUP_SCHEDULE_DATE_IN_PAST";
```

**Add only these five, and rename nothing.** The existing codes already cover the shared cases and must keep their exact string values:

- `DeliveryReceiptNotFound` — used for a missing schedule of **either** method. One table, one id space, one not-found code. Do not add `PICKUP_NOT_FOUND`.
- `DeliveryReceiptNotPending` — used for "this schedule is not Pending" on both methods. Do not add a `FULFILLMENT_SCHEDULE_NOT_PENDING` alias.
- `DeliveryQuantityExceedsAvailable` and `DeliveryReceiptConcurrencyConflict` — **the frontend matches on these exact strings** (`CreateDeliveryReceiptModal.tsx`, `CancelDeliveryModal.tsx`, `SaleDetailPage.tsx`). Changing the string would silently break those branches, since a non-matching code falls through to a generic message rather than erroring. Leave them alone.

The constant names read "Delivery…" for what are now method-neutral conditions. That is mild and deliberate: renaming buys a nicer name and costs a frontend contract change on a shipped feature. Not worth it.

- [ ] **Step 5: Authorization policy**

`AuthorizationPolicies.cs` already has `DeliveryCancel` (ManagementRoles). The spec's permission table puts "Cancel or convert" at Manager/Admin/Owner — the same set. Rename the constant to `FulfillmentCancel` (and its policy string) so it reads correctly now that it also gates pickup cancellation and conversions, update its registration and its XML doc, and update the one usage in `DeliveryReceiptsController`.

**No other policy changes.** Mapping the spec's permission table onto what exists:

| Spec row | Existing policy | Change |
|---|---|---|
| View fulfillment | `SalesView` (PosRoles) | none |
| Create Delivery/Pickup | `SalesView` (PosRoles — Cashier and up) | none |
| Mark Claimed | `SalesView` | none — spec says Cashier and up, which is what this is |
| Mark Delivered | `SalesView` | **none — deliberate.** The spec says "authorized delivery or management roles", but this app has no delivery-staff role, and the shipped feature already puts Mark Delivered at `SalesView`. Inventing a role here would be scope the spec did not ask for, and tightening it to management would be a regression in shipped behavior that the spec's "existing behavior must keep working" rule forbids. Note it as an assumption in the final report rather than changing it. |
| Cancel or convert | `DeliveryCancel` → `FulfillmentCancel` (ManagementRoles) | rename only |
| View financial totals | `ReportsView` (ManagementRoles) | none |

- [ ] **Step 6: Confirm the expected break, then commit**

Run: `dotnet build Negosio.sln`
Expected: FAILS, confined to `DeliveryReceiptService.cs`, `ReportsService.cs`/`ReportsContracts.cs`, `SalesController.cs`, `DeliveryReceiptsController.cs`, and the delivery/report test files. Anything outside that list is unexpected — investigate before committing.

```bash
git add -A
git commit -m "feat(delivery): contracts for pickup, dispositions and audited conversions"
```

---

## Task 6: `DeliveryReceiptService` — method-aware allocation, pickup, and atomic disposition handling

This is the largest and most correctness-critical task in the plan. Read it fully before writing anything.

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` (full replacement)
- Test: `tests/Negosio.IntegrationTests/Delivery/PickupTests.cs` (new)
- Test: `tests/Negosio.IntegrationTests/Delivery/FulfillmentConversionTests.cs` (new)
- Modify: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptCreateTests.cs`, `DeliveryReceiptStatusTests.cs`, `DeliveryReceiptConcurrencyTests.cs` (renamed methods/DTOs from Task 5)

**Interfaces:**
- Consumes: everything from Tasks 1-5.
- Produces: the full `IDeliveryReceiptService` implementation. Tasks 8-11 and the frontend call it.

**The shape of the rewrite.** The existing file already has the right skeleton — keep it. The changes are:

1. **`AvailabilityMap` becomes method-aware.** It currently holds `(Pending, Delivered)` per sale item against `DeliveryRequiredQuantity`. It now holds, per sale item, a `(Pending, Completed)` pair **per method**, checked against that method's intent column:

```csharp
    private sealed record MethodTotals(decimal Pending, decimal Completed);

    private sealed class AvailabilityMap
    {
        private readonly Dictionary<Guid, (decimal Delivery, decimal Pickup)> _intent;
        public Dictionary<(Guid SaleItemId, FulfillmentMethod Method), MethodTotals> Raw { get; }

        public AvailabilityMap(
            Dictionary<Guid, (decimal Delivery, decimal Pickup)> intent,
            Dictionary<(Guid, FulfillmentMethod), MethodTotals> raw)
        {
            _intent = intent;
            Raw = raw;
        }

        public MethodTotals TotalsFor(Guid saleItemId, FulfillmentMethod method) =>
            Raw.TryGetValue((saleItemId, method), out var v) ? v : new MethodTotals(0m, 0m);

        /// <summary>Quantity of <paramref name="saleItemId"/> still available to put on a NEW schedule
        /// of <paramref name="method"/>: that method's intent minus what its own pending and completed
        /// schedules already hold. Cancelled schedules are excluded upstream, which is exactly how a
        /// cancellation releases quantity.</summary>
        public decimal Available(Guid saleItemId, FulfillmentMethod method)
        {
            var intent = _intent.TryGetValue(saleItemId, out var i) ? i : (Delivery: 0m, Pickup: 0m);
            var methodIntent = method == FulfillmentMethod.Delivery ? intent.Delivery : intent.Pickup;
            var totals = TotalsFor(saleItemId, method);
            return methodIntent - totals.Pending - totals.Completed;
        }

        /// <summary>In-memory consumption during a multi-schedule batch, so two schedules in the same
        /// request cannot both claim the same units. Never persisted.</summary>
        public void Consume(Guid saleItemId, FulfillmentMethod method, decimal quantity)
        {
            var totals = TotalsFor(saleItemId, method);
            Raw[(saleItemId, method)] = totals with { Pending = totals.Pending + quantity };
        }
    }
```

`ComputeAvailabilityAsync` builds it with one query, grouping by method as well as status:

```csharp
    private async Task<AvailabilityMap> ComputeAvailabilityAsync(Guid tenantId, Sale sale, CancellationToken ct)
    {
        var saleItemIds = sale.Items.Select(i => i.Id).ToList();
        var allocations = await (
            from i in _db.DeliveryReceiptItems.AsNoTracking()
            join d in _db.DeliveryReceipts.AsNoTracking() on i.DeliveryReceiptId equals d.Id
            where i.TenantId == tenantId && saleItemIds.Contains(i.SaleItemId)
                  && d.Status != FulfillmentStatus.Cancelled
            select new { i.SaleItemId, i.Quantity, d.Method, d.Status })
            .ToListAsync(ct);

        var raw = allocations
            .GroupBy(a => (a.SaleItemId, a.Method))
            .ToDictionary(
                g => g.Key,
                g => new MethodTotals(
                    Pending: g.Where(a => a.Status == FulfillmentStatus.Pending).Sum(a => a.Quantity),
                    Completed: g.Where(a => a.Status == FulfillmentStatus.Completed).Sum(a => a.Quantity)));

        var intent = sale.Items.ToDictionary(
            i => i.Id, i => (Delivery: i.DeliveryRequiredQuantity, Pickup: i.PickupRequiredQuantity));

        return new AvailabilityMap(intent, raw);
    }
```

2. **`ValidateAndResolveLines` takes a method** and reports the method-appropriate error code (`DeliveryQuantityExceedsAvailable` / `PickupQuantityExceedsAvailable`), using `availability.Available(saleItem.Id, method)`. It still never trusts any client-supplied availability figure.

3. **`NextSequenceNumberAsync` takes a method** and filters on it — `WHERE SaleId = @saleId AND Method = @method` — so deliveries and pickups number independently.

4. **Create/CreateBatch are parameterised by method.** Write one private `CreateScheduleAsync(saleId, method, request-shaped params, ct)` and one private `CreateBatchAsync(saleId, method, batchId, schedules, ct)`, then have the four public methods (`CreateDeliveryAsync`, `CreateDeliveryBatchAsync`, `CreatePickupAsync`, `CreatePickupBatchAsync`) be thin adapters that validate their own request type and call through. Keep the existing transaction → `LockSaleItemsAsync` → post-lock idempotency re-check → availability → build → save ordering exactly as it is; it is correct and already proven under concurrency tests.

5. **`MarkClaimedAsync`** mirrors `MarkDeliveredAsync` exactly, calling `dr.MarkClaimed(...)`, pre-checking `Status != Pending` with `ErrorCodes.DeliveryReceiptNotPending` and `Method != Pickup` with `ErrorCodes.FulfillmentMethodMismatch`. `MarkDeliveredAsync` gains the mirror method check.

6. **`CancelDeliveryAsync` / `CancelPickupAsync`** are the genuinely new logic. Both follow one shared private path:

```csharp
    /// <summary>
    /// Cancels a pending schedule and applies its disposition — all inside one transaction, with the
    /// sale-items lock held, so the cancellation, the intent conversion, the audit event and any
    /// replacement schedule either all commit or none do.
    /// </summary>
    private async Task<CancellationResultDto> CancelWithDispositionAsync(
        Guid id,
        FulfillmentMethod expectedMethod,
        string reason,
        CancellationDisposition disposition,
        FulfillmentMethod convertToMethod,      // == expectedMethod when the quantities just go back to unscheduled
        bool completeReplacementImmediately,    // true only for CustomerPickedUpInstead
        Func<Sale, string, DeliveryReceipt>? buildReplacement,  // null when there is no replacement
        CancellationToken ct)
```

The body, in order:

- `RequireTenant()`, load the schedule **tracked** with `.Include(d => d.Items)`, 404 if missing, `GuardBranchAsync`.
- Reject if `dr.Method != expectedMethod` → `BusinessRuleException(ErrorCodes.FulfillmentMethodMismatch, ...)`.
- Reject if `dr.Status != FulfillmentStatus.Pending` → `BusinessRuleException(ErrorCodes.DeliveryReceiptNotPending, ...)`. **This is what makes a Delivered delivery and a Claimed pickup un-cancellable and un-convertible**, per spec tests 22 and 23.
- Open the transaction, then `LockSaleItemsAsync(tenantId, dr.SaleId!.Value, ct)` — the same lock every allocation path takes, so a conversion can't race an allocation.
- **Re-read the schedule's status after the lock** and bail with `ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict)` if it is no longer Pending; a concurrent mark-delivered/claim may have committed while we waited.
- `dr.Cancel(_currentUser.UserId, reason, disposition, nowUtc)` — the domain re-validates the method/disposition pairing.
- For each of `dr.Items`: if `convertToMethod != expectedMethod`, call `saleItem.ConvertFulfillment(expectedMethod, convertToMethod, item.Quantity)` on the **tracked** `SaleItem`. Then, either way, write a `FulfillmentConversion.Record(...)` row with `FromMethod = expectedMethod`, `ToMethod = convertToMethod`, `SourceRecordId = dr.Id`, `ReplacementRecordId = replacement?.Id`, the cancellation reason, and the same `nowUtc`. A same-method event (a plain release) is still recorded — the audit trail should show *why* quantity returned to unscheduled, not just that it did.
- If `buildReplacement` is non-null: build it, assign the next sequence number **for the target method**, copy each cancelled item across (same `SaleItemId`, same quantity, same snapshots), and if `completeReplacementImmediately` call `replacement.MarkClaimed(_currentUser.UserId, nowUtc)` so it is created already Completed. Add it to `_db.DeliveryReceipts`.
- One `SaveChangesAsync`, wrapped in the existing `catch (DbUpdateConcurrencyException)` → rollback → `ConflictException` and `catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))` → rollback → `ConflictException` pair. The unique index on `(SourceRecordId, SaleItemId)` from Task 4 is what makes a retried cancellation collide here instead of double-converting.
- Commit, then map both records to DTOs.

The five public entry points map onto it like this:

| Caller | expectedMethod | disposition | convertToMethod | completeImmediately | buildReplacement |
|---|---|---|---|---|---|
| `CancelDeliveryAsync` | Delivery | `DeliverLater` | Delivery | false | null |
| `CancelDeliveryAsync` | Delivery | `ConvertToPickup` | Pickup | false | pickup from `request.Replacement` |
| `CancelDeliveryAsync` | Delivery | `CustomerPickedUpInstead` | Pickup | **true** | pickup from `request.Replacement` |
| `CancelPickupAsync` | Pickup | `PickupLater` | Pickup | false | null |
| `CancelPickupAsync` | Pickup | `ConvertToDelivery` | Delivery | false | delivery from `request.Replacement` |

Reject a null `request.Replacement` for the three dispositions that need one with `BusinessRuleException(ErrorCodes.ReplacementDetailsRequired, ...)`, and a non-null one for the two that don't with `ErrorCodes.InvalidCancellationDisposition`.

**Do not validate the replacement's date against business-today for `CustomerPickedUpInstead`** — the collection already happened, so a past date is correct there. Do validate it for `ConvertToPickup` and `ConvertToDelivery` (those are future schedules) using the existing `EnsureNotPastBusinessToday`.

7. **`GetSaleFulfillmentAsync`** now builds the 8-bucket `SaleItemFulfillmentDto` per line (take-now from `SaleItem.TakeNowQuantity`; the six method buckets from `availability`), splits schedules into `Deliveries`/`Pickups` by `Method`, loads the sale's `FulfillmentConversions` (newest first, joined to `Users` for `CreatedByName` and to `sale.Items` for the product name), computes both `CanCreate*` gates, and calls the new 9-argument `SaleFulfillmentCalculator.Derive`. `hasOverduePendingSchedule` is any Pending schedule of **either** method whose `ScheduledDate < BusinessToday()`. **Include every sale line that has any delivery or pickup intent** — not only delivery, as the current code does.

8. **`MapToDtoAsync`** returns `FulfillmentScheduleDto`: add `Method`, `CancellationDisposition`, the renamed completed fields, and force `DeliveryCharge` to `0m` when `Method == Pickup` (a pickup never carries one — the charge still lives on the Sale untouched, this is purely what this DTO reports).

- [ ] **Step 1: Write the failing tests**

Create `tests/Negosio.IntegrationTests/Delivery/PickupTests.cs` covering, each as its own `[Fact]` against real HTTP endpoints (routes land in Task 8; until then these fail with 404, which is the expected first-run failure):

- Creating a pickup with `pickupRequiredQuantity` available succeeds, is `Pending`, `Method == Pickup`, has no address, and gets `SequenceNumber == 1` **even when the sale already has Delivery 1** (proving per-method numbering).
- Creating a pickup for more than the pickup-available quantity is rejected 400.
- Creating a pickup for a line whose quantity was marked for *delivery* (not pickup) is rejected 400 — the two pools are separate.
- A pending pickup can be marked claimed: 200, `Status == Completed`, `CompletedAtUtc` and `CompletedByName` set.
- Marking claimed twice is rejected 400; marking a *delivery* claimed is rejected 400; marking a *pickup* delivered is rejected 400.
- A claimed pickup cannot be cancelled (400) — terminal.
- The claimed quantity no longer appears as available to schedule.

Create `tests/Negosio.IntegrationTests/Delivery/FulfillmentConversionTests.cs` covering:

- Delivery cancelled with `DeliverLater`: delivery is `Cancelled` with that disposition, no replacement returned, quantity is delivery-available again, pickup-available unchanged, and a same-method conversion row exists.
- Delivery cancelled with `ConvertToPickup`: delivery `Cancelled`, a **new Pending Pickup** is returned with the replacement's details and the same item quantities, the sale item's `DeliveryRequiredQuantity` dropped and `PickupRequiredQuantity` rose by the same amount, and a `Delivery→Pickup` conversion row links `SourceRecordId`/`ReplacementRecordId`.
- Delivery cancelled with `CustomerPickedUpInstead`: delivery is `Cancelled` with that disposition and **not** `Completed`; the replacement pickup is `Completed` with `CompletedAtUtc`/`CompletedByName` set; take-now on the line is **unchanged** (spec test 18 — no take-now adjustment is created); the conversion row is `Delivery→Pickup`.
- Pickup cancelled with `PickupLater` releases back to pickup-unscheduled.
- Pickup cancelled with `ConvertToDelivery` creates a new Pending Delivery and moves intent the other way.
- Cancelling a delivery with `PickupLater` (wrong method) is rejected 400; with a null `Replacement` on `ConvertToPickup` is rejected 400.
- **`"TakeNow"` is not an accepted disposition value** (spec test 4). Post a raw cancel body with `"disposition": "TakeNow"` to both the delivery and the pickup cancel endpoints and assert 400 on each. The enum has no such member, so this lands as a deserialization/validation failure — that is the correct outcome and the test documents that it can never become one.
- A delivery that is already `Completed` cannot be cancelled (400).
- A pickup that is already `Completed` (Claimed) cannot be cancelled or converted (400) — spec test 23.
- **Marking a pickup Claimed does not cancel it** (spec test 7): after `MarkClaimed`, `Status == Completed`, `CancelledAtUtc` is null and `CancellationDisposition` is null.
- **A cancelled schedule stays in history** (spec tests 10, 24): the cancelled row is still returned by `listForSale` and by the sale summary, and its quantity is not counted as pending or completed anywhere.
- `Sales.DeliveryCharge` is byte-identical before and after a `ConvertToPickup` (spec test 26), and the pickup's own DTO reports `DeliveryCharge == 0`.

Then update the three existing delivery test files for the renamed DTOs/methods (`CreateDeliveryAsync` route bodies now use `ScheduledDate`/`Notes`; `CancelDeliveryRequest` now needs a `Disposition`).

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "PickupTests|FulfillmentConversionTests"`
Expected: fails to compile (service interface not implemented yet).

- [ ] **Step 3: Rewrite the service per the eight points above**

- [ ] **Step 4: Close the remaining compile breaks — mechanically, not featurefully**

Tasks 1-5 renamed types the controllers and the reports layer still reference. The next task generates the EF migration, and `dotnet-ef` builds the **entire** solution including the API startup project to do it — so the whole solution has to compile before that task can even start. This step is what makes that true. It is deliberately scoped to *renames and signature adaptation only*; the actual pickup features in these files land in Tasks 8-11.

Files and the minimum change each needs:

- `src/Negosio.Api/Controllers/DeliveryReceiptsController.cs` — update to the new DTO names and the renamed `FulfillmentCancel` policy. `POST /{id}/cancel` now needs a `CancelDeliveryRequest` body; pass it straight through to `CancelDeliveryAsync` and return the `CancellationResultDto`. Do **not** add pickup routes yet (Task 8).
- `src/Negosio.Api/Controllers/SalesController.cs` — the fulfillment endpoint now returns `SaleFulfillmentSummaryDto`; adapt the return type only.
- `src/Negosio.Application/Reports/ReportsContracts.cs` and `ReportsService.cs` — swap `DeliveryStatus` for `FulfillmentStatus`, `Delivered`/`DeliveredAtUtc` for `Completed`/`CompletedAtUtc`, `ScheduledDeliveryDate` for `ScheduledDate`. Where a query currently selects every `DeliveryReceipt`, add `.Where(d => d.Method == FulfillmentMethod.Delivery)` so the existing delivery report keeps meaning what it meant before pickups existed. The new pickup and combined views are Task 11.
- `src/Negosio.Application/Pos/CheckoutService.cs` and `Pos/CheckoutContracts.cs` — if Task 2's `Sale.AddItem` signature change reaches here, pass `0m` for `pickupRequiredQuantity` for now. Real 3-way allocation is Task 9.
- Any test file still referencing renamed members.

Resist the urge to implement the later tasks' features while you are in these files. A rename-only diff is easy to review; a rename-plus-features diff is where cross-task bugs hide.

- [ ] **Step 5: Build — this one must succeed**

Run: `dotnet build Negosio.sln`
Expected: **0 errors.** This is the task's real checkpoint and the precondition for Task 7. If anything still fails, fix it here rather than deferring it.

- [ ] **Step 6: Run the suite and record what fails**

Run: `dotnet test`
Expected: the pre-existing suite passes; the new `PickupTests` and `FulfillmentConversionTests` **fail at runtime** with SQL errors about missing columns (`PickupRequiredQuantity`, `Method`, `FulfillmentConversions`). That is correct and expected — the schema does not exist until Task 7 applies the migration. Record the failure count and confirm every failure is one of those two new files. A failure anywhere else is a real regression and must be fixed before committing.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(delivery): pickup scheduling, claiming and atomic disposition-driven conversions"
```

---

## Task 7: EF migration — method discriminator, pickup intent, conversion log

> **Why this task sits here and not right after the EF config (Task 4):** `dotnet-ef` has to build the
> whole solution, including the API startup project, to generate a migration. Tasks 3 and 5 deliberately
> leave the application and API layers non-compiling, and Task 6 Step 4 is what closes them. Generating
> the migration before that lands would simply fail. (A prior plan in this repo hit exactly this and had
> to reorder mid-execution — this ordering is that lesson applied up front.)

**Files:**
- Create: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/<timestamp>_AddPickupFulfillment.cs` (+ `.Designer.cs`) — generated, then inspected
- Modify: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/TenantDbContextModelSnapshot.cs` (auto — do not hand-edit)

**Interfaces:** produces the real schema every later task runs against.

The previous fulfillment migration in this repo (`AddDeliveryFulfillment`) needed heavy hand-editing because it backfilled legacy rows. **This one should need far less** — every change here is either a new nullable column, a new column with a safe default, a new table, or an index swap. Read what EF generates before assuming it's fine, but do not restructure it into phases unless a specific statement actually requires it.

- [ ] **Step 1: Generate**

```bash
dotnet tool restore
dotnet dotnet-ef migrations add AddPickupFulfillment --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
```

The solution builds cleanly at this point (Task 6 Step 5 is that gate). If generation fails on a build error, stop and report BLOCKED with the exact compiler output rather than patching around it — a broken build here means Task 6 left something undone.

- [ ] **Step 2: Inspect the generated `Up()` and verify each statement**

Expected statements, and what to check for each:
- `AddColumn<int>("Method", "DeliveryReceipts", defaultValue: 2)` — **2 is `FulfillmentMethod.Delivery`.** Every existing row is a delivery, so this default is the backfill; confirm it emitted `2` and not `0`/`1`. If it emitted something else, the `HasDefaultValue(FulfillmentMethod.Delivery)` in Task 4 didn't take effect — fix the config rather than hand-patching the number.
- `RenameColumn` × 4 — `ScheduledDeliveryDate`→`ScheduledDate`, `DeliveredAtUtc`→`CompletedAtUtc`, `DeliveredByUserId`→`CompletedByUserId`, `DeliveryNotes`→`Notes`. EF should emit `RenameColumn` (preserving data), **not** a drop+add pair. If it emitted drop+add, replace those with `migrationBuilder.RenameColumn(...)` by hand — a drop+add would silently destroy every existing delivery's scheduled date and delivered-at audit trail.
- `AlterColumn<string>("DeliveryAddress", nullable: true, oldNullable: false)` — widening, always safe.
- `AddColumn<int>("CancellationDisposition", "DeliveryReceipts", nullable: true)` — no backfill: existing cancelled deliveries genuinely have no recorded disposition (they predate the concept), and inventing one would be fabricating history. The UI shows "—" for them.
- `AddColumn<decimal>("PickupRequiredQuantity", "SaleItems", precision 18/3, defaultValue: 0m)` — every existing sale item had no pickup allocation, so 0 is exactly right and no false pickup backlog is created.
- `CreateTable("FulfillmentConversions", ...)` + its four indexes + two FKs.
- `DropIndex`/`CreateIndex` for the two replaced `DeliveryReceipts` indexes.

- [ ] **Step 3: Generate and read the SQL before applying**

```bash
dotnet dotnet-ef migrations script --idempotent --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output ../../add-pickup.sql
```

(Windows machine — write the script somewhere outside the repo tree or into your scratchpad; do not commit it.) Read the generated SQL and confirm: the renames are `sp_rename` calls (data-preserving), `Method` gets `DEFAULT 2`, `PickupRequiredQuantity` gets `DEFAULT 0.0`, and nothing drops a column that holds data.

- [ ] **Step 4: Apply and spot-check**

```bash
dotnet dotnet-ef database update --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api
```

Then, against a real tenant database that already has delivery rows (find one via the platform DB's `TenantDatabases` table — the previous migration's report confirmed ~25 exist locally), verify with direct queries:
- Every existing `DeliveryReceipts` row has `Method = 2`.
- `ScheduledDate` still holds each row's original date (renamed, not recreated) and `CompletedAtUtc` still holds the original delivered timestamps — this is the check that proves the rename preserved data.
- Every `SaleItems` row has `PickupRequiredQuantity = 0`.
- `FulfillmentConversions` exists and is empty.
- No row anywhere has a non-null `CancellationDisposition` yet.

Report the actual row counts you saw, not a summary — if a tenant database has zero delivery rows, say so, because then the rename check proved nothing there and you need to find one that does.

- [ ] **Step 5: Run the full backend suite**

Run: `dotnet test`
Expected: **everything green, including the `PickupTests` and `FulfillmentConversionTests` that failed on missing columns at the end of Task 6** — applying this migration is precisely what fixes them. If one of those tests now fails for a *logic* reason rather than a schema reason, that is a real defect in Task 6's service: report it rather than adjusting the test.

This is the plan's first fully-green checkpoint. Record the exact pass count.

- [ ] **Step 6: Commit**

```bash
git add src/Negosio.Infrastructure/Persistence/Migrations/Tenant/
git commit -m "feat(db): migration for pickup fulfillment, method discriminator and the conversion log"
```

---

## Task 8: API surface — pickup routes, claim, and the two disposition-aware cancel endpoints

**Files:**
- Modify: `src/Negosio.Api/Controllers/DeliveryReceiptsController.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/PickupTests.cs`, `FulfillmentConversionTests.cs` (already written in Task 6 — they exercise these routes)

**Interfaces:**
- Consumes: `IDeliveryReceiptService` (Task 5/6), `AuthorizationPolicies.FulfillmentCancel` (Task 5).
- Produces: the HTTP contract the frontend (Tasks 13-18) consumes.

The controller currently has delivery routes only. Add the pickup mirror and the claim/cancel verbs. **`POST /{id}/{verb}` for every state change — this codebase never uses PATCH.**

Routes after this task (class-level `[Authorize(Policy = AuthorizationPolicies.SalesView)]` stays):

| Verb + route | Body | Returns | Policy |
|---|---|---|---|
| `POST /api/sales/{saleId}/delivery-receipts` | `CreateDeliveryReceiptRequest` | `FulfillmentScheduleDto` | class-level |
| `POST /api/sales/{saleId}/delivery-receipts/batch` | `CreateDeliveryReceiptBatchRequest` | `FulfillmentBatchResultDto` | class-level |
| `POST /api/sales/{saleId}/pickups` | `CreatePickupRequest` | `FulfillmentScheduleDto` | class-level |
| `POST /api/sales/{saleId}/pickups/batch` | `CreatePickupBatchRequest` | `FulfillmentBatchResultDto` | class-level |
| `GET /api/sales/{saleId}/delivery-receipts` | — | `FulfillmentScheduleDto[]` | class-level — **now returns pickups too** |
| `GET /api/sales/{saleId}/fulfillment` | — | `SaleFulfillmentSummaryDto` | class-level *(on `SalesController`)* |
| `GET /api/delivery-receipts/{id}` | — | `FulfillmentScheduleDto` | class-level |
| `POST /api/delivery-receipts/{id}/deliver` | — | `FulfillmentScheduleDto` | class-level |
| `POST /api/delivery-receipts/{id}/claim` | — | `FulfillmentScheduleDto` | class-level |
| `POST /api/delivery-receipts/{id}/cancel` | `CancelDeliveryRequest` | `CancellationResultDto` | `FulfillmentCancel` |
| `POST /api/pickups/{id}/cancel` | `CancelPickupRequest` | `CancellationResultDto` | `FulfillmentCancel` |

**Two renames in this table.** `GET /api/sales/{saleId}/delivery-summary` becomes `GET /api/sales/{saleId}/fulfillment`, because it no longer describes deliveries alone. `GET /api/sales/{saleId}/delivery-receipts` keeps its path (it is the schedule list and the path is baked into the frontend) but now returns **both** methods, ordered by `Method` then `SequenceNumber`. Task 13 updates the frontend client for the renamed route; nothing else consumes it. Do not leave a deprecated alias behind — this is a pre-release app with one client.

Design notes the implementer must not "improve" on:
- **One controller, not two.** Deliveries and pickups are one table and one service; splitting the controller would duplicate the branch guard and the error mapping for no gain. Add the pickup actions inside the existing controller with their own `[HttpPost("~/api/pickups/{id:guid}/cancel")]`-style absolute routes.
- **`GET /api/delivery-receipts/{id}` serves pickups too** — the id is unique across both methods, and the DTO carries `Method`. The print page uses this one endpoint for both. Do not add `GET /api/pickups/{id}`.
- `/deliver` and `/claim` are separate routes on purpose. A single `/complete` would leave the API unable to reject "mark this delivery claimed", which the spec requires as an error.
- Cancel is split by method because the request bodies differ (`PickupReplacementInput` vs `DeliveryReplacementInput`) and the valid disposition sets differ. One endpoint taking a union would push that validation out of the type system.
- Every action keeps the existing `GuardBranchAsync`-backed 404-not-403 behavior by virtue of calling the service; the controller adds no branch logic of its own.

- [ ] **Step 1: Add the routes**

Follow the existing actions' exact shape — `[HttpPost("{id:guid}/claim")]`, `ActionResult<FulfillmentScheduleDto>`, `await _service.MarkClaimedAsync(id, ct)`, no try/catch (the global exception middleware maps `AppException` subclasses to status codes).

- [ ] **Step 2: Run the pickup and conversion tests**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "PickupTests|FulfillmentConversionTests"`
Expected: **all pass.** These tests were written in Task 6 against these exact routes; this is where they finally go green end-to-end. If one fails, the defect is real — do not adjust the test to match the implementation.

- [ ] **Step 3: Full suite**

Run: `dotnet test`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(api): pickup, claim and disposition-aware cancellation endpoints"
```

---

## Task 9: Checkout — three-way allocation at the point of sale

**Files:**
- Modify: `src/Negosio.Application/Pos/CheckoutContracts.cs` (holds `CheckoutItemRequest`)
- Modify: `src/Negosio.Application/Pos/CheckoutService.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutValidator.cs` *(singular — that is the real filename)*
- Test: `tests/Negosio.IntegrationTests/Pos/CheckoutAllocationTests.cs` (new)
- Modify: `tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryFulfillmentTests.cs` and `CheckoutDeliveryChargeTests.cs` — existing coverage of the shipped delivery allocation; they must keep passing untouched except for any signature change

**Interfaces:**
- Consumes: `Sale.AddItem(..., deliveryRequiredQuantity, pickupRequiredQuantity)` (Task 2).
- Produces: the checkout wire contract the POS (Task 15) sends.

This is where the spec's core allocation identity gets enforced at its source:

> `Sold = TakeNow + Delivery-intended + Pickup-intended`

- [ ] **Step 1: Write the failing tests**

`CheckoutAllocationTests.cs`, each a `[Fact]` posting to `/api/pos/checkout`:

1. Item with `quantity: 10, deliveryRequiredQuantity: 4, pickupRequiredQuantity: 3` → succeeds; the created `SaleItem` has `DeliveryRequiredQuantity == 4`, `PickupRequiredQuantity == 3`, `TakeNowQuantity == 3`.
2. `quantity: 5, delivery: 5, pickup: 0` → `TakeNowQuantity == 0`. (Pure delivery still works — regression guard for the shipped feature.)
3. `quantity: 5, delivery: 0, pickup: 5` → `TakeNowQuantity == 0`, and the sale's fulfillment summary reports `CanCreatePickup == true`, `CanCreateDelivery == false`.
4. `quantity: 5, delivery: 0, pickup: 0` → `TakeNowQuantity == 5`; the sale's fulfillment status is `NotApplicable`.
5. `quantity: 5, delivery: 3, pickup: 3` (sum 6 > 5) → **400** and no sale is created. Assert the status and the validation message, **not** an error code: this check lives in `CheckoutValidator` as a plain FluentValidation rule (that is how the existing delivery-quantity rule works) and surfaces as a `ValidationAppException` without a code. Do not invent an `ErrorCodes` member for it.
6. Omitting `pickupRequiredQuantity` entirely from the JSON body behaves as 0 — **the existing POS client must keep working unchanged.** Assert this by posting a body with no `pickupRequiredQuantity` key at all.
7. Negative `pickupRequiredQuantity` → 400.
8. A checkout carrying `deliveryCharge: 100` with a mixed delivery+pickup allocation stores `Sales.DeliveryCharge == 100` exactly once — not per item, not per method.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Negosio.IntegrationTests --filter CheckoutAllocationTests`

- [ ] **Step 3: Implement**

- Add `decimal PickupRequiredQuantity` to the checkout item request record **with a default of `0m`** so an older client body deserializes cleanly. (`CheckoutItemRequest` is a `record`; give the positional parameter a default, or use an init property initialized to `0m` — match whatever the surrounding records already do.)
- Validator: `PickupRequiredQuantity >= 0`, and `DeliveryRequiredQuantity + PickupRequiredQuantity <= Quantity` with the message "Delivery and pickup quantities together cannot exceed the item quantity."
- `CheckoutService`: pass it through to `Sale.AddItem`. Task 2's domain guard is the backstop; this validator is the friendly 400.
- Nothing else in `CheckoutService` changes. **Do not touch the delivery-charge code path at all** — it is already correct and the spec requires it stay byte-identical.

- [ ] **Step 4: Verify + commit**

Run: `dotnet test`
Expected: green.

```bash
git add -A
git commit -m "feat(pos): three-way take-now / delivery / pickup allocation at checkout"
```

---

## Task 10: Reports — pickup view and combined fulfillment view

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Test: `tests/Negosio.IntegrationTests/Reports/FulfillmentReportTests.cs` (new)

**Interfaces:**
- Consumes: `FulfillmentMethod`, `FulfillmentStatus`, `SaleFulfillmentCalculator` (Tasks 1, 5).
- Produces: three report endpoints the frontend (Task 18) renders.

Three views, per the spec. Task 6 Step 4 already scoped the *existing* delivery view to `Method == Delivery`; this task adds the two new ones.

| Endpoint | Rows | Notes |
|---|---|---|
| `GET /api/reports/delivery` | one per delivery schedule | **unchanged behavior** — a regression surface, not a feature surface |
| `GET /api/reports/pickup` | one per pickup schedule | same filters and shape, `Method == Pickup`, no address column, no delivery charge |
| `GET /api/reports/fulfillment` | one per **sale item per method** | combined allocation view + a summary block |

All three take the existing report filter set (date range via `ReportPeriodResolver`, branch, status) so the frontend's shared filter bar works against any of them. **All filtering, sorting, pagination and summaries are backend-driven** — the spec says so explicitly, and the frontend must not sort or total anything itself.

**The combined view's row shape, from the spec:** sale number, sale item, quantity, fulfillment method, status, scheduled/expected date, completion date, recipient/customer, source schedule id, replacement schedule id. So a row is one *allocation* — "3 × Coke, Delivery, Pending, due 16 Sep" — not one sale. A sale with two lines split across both methods produces several rows. Unscheduled quantity produces a row too, with `Status = Unscheduled`, a null date and no schedule id; this is the one place `FulfillmentStatus.Unscheduled` legitimately appears, and it is a projection, never a stored row.

Alongside the rows, the endpoint returns a summary block with a total for each of: taken now, delivery unscheduled, delivery pending, delivered, pickup unscheduled, pickup pending, claimed, cancelled schedules, fully-fulfilled sales, sales needing attention. The last two are sale counts derived with `SaleFulfillmentCalculator.Derive`, so the report and the Sale-detail page can never disagree.

**Pickup report filters**, per the spec: Today, Upcoming, Overdue, Pending, Claimed, Cancelled, plus customer, contact and sale-number search. The delivery report's existing preset enum already covers the shape — mirror it, substituting `Claimed` for `Delivered`.

**The financial correctness rule that must not regress:** the shipped delivery report had a real double-counting bug — summing `Sale.DeliveryCharge` across *schedules* multiplied it by the number of schedules on the sale. It was fixed by aggregating over **distinct sales**. The combined view is exactly where that bug wants to come back, because a sale can now have both deliveries and pickups. Compute any delivery-charge total by `.Select(x => new { x.SaleId, x.DeliveryCharge }).Distinct()` — or an equivalent group-by-sale — never by summing a per-schedule projection. Test 4 below exists specifically to catch a reintroduction.

- [ ] **Step 1: Write the failing tests**

`FulfillmentReportTests.cs`:

1. `GET /api/reports/pickup` returns only pickup schedules; a sale with one delivery and one pickup contributes exactly one row.
2. `GET /api/reports/delivery` on that same sale returns exactly one row — pickups never leak into the delivery view.
3. Every pickup row reports `DeliveryCharge == 0`.
4. **Delivery charge is not multiplied:** a sale with `DeliveryCharge = 100`, **three** delivery schedules and **two** pickup schedules reports a combined-view total of exactly `100`.
5. `GET /api/reports/fulfillment` on a sale with 10 Coke split 3 take-now / 4 delivery (2 delivered, 2 pending) / 3 pickup (all unscheduled) returns **four** rows for that line — TakeNow/Completed 3, Delivery/Completed 2, Delivery/Pending 2, Pickup/Unscheduled 3 — and the row quantities sum to 10.
6. The combined summary block's ten totals are correct for a fixture with both methods, a cancellation and a conversion — and `fullyFulfilledSales` / `salesNeedingAttention` match `SaleFulfillmentCalculator.Derive` run over the same fixture.
7. **A delivery cancelled via `CustomerPickedUpInstead` still reports as Cancelled in the delivery view and is not counted as a successful delivery** (spec test 29).
8. **The pickup created by `CustomerPickedUpInstead` reports as Claimed in the pickup view** (spec test 30).
9. All three endpoints require the `ReportsView` policy — a Cashier gets 403.
10. A branch-scoped user sees only their branch's rows in all three views.
11. Tenant isolation: a second tenant's sales never appear in any of the three views.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Negosio.IntegrationTests --filter FulfillmentReportTests`

- [ ] **Step 3: Implement**

Mirror the existing delivery-report method for pickup — same filter plumbing, same paging, same branch scoping — rather than inventing a new shape. For the combined view, project per sale and reuse `SaleFulfillmentCalculator.Derive` so the report and the Sale-detail page can never disagree about a sale's status. Register the two new routes on `ReportsController` beside the existing one, keeping `[Authorize(Policy = AuthorizationPolicies.ReportsView)]`.

- [ ] **Step 4: Verify + commit**

Run: `dotnet test`
Expected: green.

```bash
git add -A
git commit -m "feat(reports): pickup schedule view and combined fulfillment view"
```

---

## Task 11: Concurrency and idempotency coverage for the new paths

**Files:**
- Modify: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs`

**Interfaces:** consumes everything; produces no new production code. If a test here fails, the defect is in Task 6 and gets fixed there.

The delivery feature's concurrency model is already proven. What is *not* yet proven is pickup allocation and — most importantly — conversion, which mutates the `SaleItem` intent columns that the allocation path reads under a lock.

- [ ] **Step 1: Write the tests**

1. **Concurrent pickup creation cannot over-allocate.** Two simultaneous requests each scheduling the full remaining pickup quantity: exactly one succeeds, the other gets 400 `PickupQuantityExceedsAvailable`. (Same shape as the existing delivery test — this exercises the pessimistic `UPDLOCK, HOLDLOCK` path.)
2. **A conversion cannot race an allocation.** Concurrently: (a) cancel a delivery with `ConvertToPickup`, (b) create a new delivery for the quantity that conversion is about to move away. Exactly one succeeds; afterwards `DeliveryRequiredQuantity + PickupRequiredQuantity` still equals what it should and never exceeds `Quantity`.
3. **Two concurrent cancels of the same schedule:** one succeeds, the other does not. Assert "not both succeeded" plus a non-2xx for the loser — **do not** assert an exact status, because which guard fires (409 conflict vs 400 not-pending) depends on interleaving.
4. **Conversion is not double-applied on retry.** Issue the same cancel-with-conversion twice. The second must not create a second replacement schedule and must not move intent twice. (The unique index on `(SourceRecordId, SaleItemId)` from Task 4 enforces this — assert the *end state*, not the second call's status.)
5. **Pickup batch idempotency.** Posting the same `BatchRequestId` twice creates the schedules once and returns `WasExistingBatch == true` the second time.
6. **Batch idempotency is scoped to the sale.** The same `BatchRequestId` against a *different* sale creates new schedules rather than returning the first sale's.
7. **Cancel races mark-delivered.** Concurrently cancel a pending delivery and mark it delivered. Exactly one wins; the row ends in exactly one of `Cancelled` or `Completed`, never a mix (e.g. cancelled *and* carrying `CompletedAtUtc`). Assert the final row state, not the two responses.
8. **Cancel races mark-claimed** — the same test for a pending pickup.
9. **No duplicate sequence numbers.** Fire N concurrent create-pickup requests against one sale. Every created row has a distinct `SequenceNumber` within `(SaleId, Method)`. The unique index from Task 4 is the guarantee; this test proves the service doesn't turn it into a 500. *(If this test fails with a unique-violation surfacing as an unhandled exception, the fix is in Task 6 — map it to a retry or a `ConflictException`, do not drop the index.)*
10. **Cross-tenant items are rejected.** Submit a schedule whose `saleItemId` belongs to another tenant's sale: 404 (not 403, not 500), and nothing is created.
11. **A failed replacement rolls back the cancellation** (spec test 20). Force the replacement insert to fail — the cleanest trigger is a replacement referencing a sale item that does not belong to the sale, which the service rejects after the source record has already been marked cancelled in the change tracker. Assert afterwards that the source schedule is **still Pending**, no conversion row exists, and no replacement exists. This is the single most important test in this task: it is what proves the transaction boundary is real.

- [ ] **Step 2: Run**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptConcurrencyTests`
Expected: green. A failure here is a genuine correctness bug — report it rather than relaxing the assertion.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "test: concurrency and idempotency coverage for pickup and conversions"
```

---

## Task 12: Backend regression checkpoint

**Files:** none — this is a gate, not a change (except for gap-filling tests found in Step 2).

- [ ] **Step 1: Full suite**

Run: `dotnet test`
Expected: **everything green.** Record the exact count.

- [ ] **Step 2: Walk the spec's 40 required tests**

Open `docs/superpowers/specs/2026-09-13-pickup-fulfillment.md`, find the numbered test list, and produce a table: test number → the test method that covers it → pass/fail. The UI-only items are covered by Tasks 13-19 — mark those "deferred to frontend" rather than inventing backend coverage for them.

**Any backend item with no covering test is a gap: write the test now, in this task.** This checkpoint exists precisely to catch what the per-task tests missed.

- [ ] **Step 3: Verify the delivery-charge invariant by hand**

Against a real tenant DB, find a sale with a non-zero `DeliveryCharge` and multiple schedules, and confirm with a direct SQL query that `Sales.DeliveryCharge` holds exactly one value and that no `DeliveryReceipts` row stores a charge of its own. Paste the actual query output into the report.

- [ ] **Step 4: Commit anything written**

```bash
git add -A
git commit -m "test: close spec coverage gaps found at the backend checkpoint"
```

---

> **Frontend tasks (13-19) — read this before starting any of them.**
>
> There is **no frontend test runner** in this repo. `npx tsc -b` from `web/negosio-web` is the only automated check, and it is not optional — run it at the end of every frontend task. A bare `tsc --noEmit` silently checks **zero files** here, because the project uses TypeScript project references with `"files": []` in the root tsconfig. Use `-b`.
>
> **Numeric inputs use the draft-commit-on-blur pattern.** `components/pos/CartItem.tsx` is the reference implementation (`qtyDraft` / `commitQty`). A numeric input that does `onChange={e => setX(Number(e.target.value) || 0)}` **breaks decimal entry** — the user cannot type "0." because it coerces to 0 on every keystroke. This exact bug was shipped and fixed once already in the delivery work. Every new quantity input in Tasks 14-16 must hold a `string | null` draft in its own local state and commit on blur.
>
> **`useEffect` dependency arrays on modal prefill:** depend on `[open]` only. Adding the prefill object or the available-items array to the deps re-runs the effect whenever the parent re-renders and silently wipes whatever the user has typed. Also shipped-and-fixed once already.
>
> Follow the codebase's existing component idiom (`components/ui` primitives, `usePagedQuery`, `useCan`, TanStack Query with `invalidateQueries` on mutation success). Every mutation needs an `onError` — a mutation without one fails silently.

---

## Task 13: Frontend foundation — types, API client, and method-aware labels

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/api/deliveryReceipts.ts` → rename to `web/negosio-web/src/api/fulfillment.ts`
- Modify: `web/negosio-web/src/lib/pos.ts`
- Modify: `web/negosio-web/src/components/sales/DeliveryStatusBadge.tsx` → rename to `FulfillmentStatusBadge.tsx`

**Interfaces:**
- Consumes: the HTTP contract from Tasks 8 and 10.
- Produces: every type and helper Tasks 14-18 import. Get the names right here; the rest of the frontend is downstream of this file.

- [ ] **Step 1: Replace the delivery block in `types.ts`**

Mirror the backend DTOs exactly, camelCased. Enums arrive from ASP.NET as **strings** (the existing `DeliveryStatus` is a string union — keep that convention):

```ts
// ---- Fulfillment ----
// Backend contract (SalesController / DeliveryReceiptsController):
//   POST /api/sales/{saleId}/delivery-receipts        -> 201, FulfillmentScheduleDto
//   POST /api/sales/{saleId}/delivery-receipts/batch  -> 201, FulfillmentBatchResultDto
//   POST /api/sales/{saleId}/pickups                  -> 201, FulfillmentScheduleDto
//   POST /api/sales/{saleId}/pickups/batch            -> 201, FulfillmentBatchResultDto
//   GET  /api/sales/{saleId}/delivery-receipts        -> 200, FulfillmentScheduleDto[]  (both methods)
//   GET  /api/sales/{saleId}/fulfillment              -> 200, SaleFulfillmentSummaryDto
//   GET  /api/delivery-receipts/{id}                  -> 200 | 404  (serves pickups too)
//   POST /api/delivery-receipts/{id}/deliver          -> 200, FulfillmentScheduleDto
//   POST /api/delivery-receipts/{id}/claim            -> 200, FulfillmentScheduleDto
//   POST /api/delivery-receipts/{id}/cancel           -> 200, CancellationResultDto  (Owner/Admin/Manager)
//   POST /api/pickups/{id}/cancel                     -> 200, CancellationResultDto  (Owner/Admin/Manager)

export type FulfillmentMethod = 'TakeNow' | 'Delivery' | 'Pickup'
export type FulfillmentStatus = 'Unscheduled' | 'Pending' | 'Completed' | 'Cancelled'

export type CancellationDisposition =
  | 'DeliverLater'
  | 'PickupLater'
  | 'ConvertToDelivery'
  | 'ConvertToPickup'
  | 'CustomerPickedUpInstead'

export interface FulfillmentItemInput {
  saleItemId: string
  quantity: number
}

export interface FulfillmentItemDto {
  saleItemId: string
  productName: string
  variantName: string | null
  quantity: number
  unitPrice: number | null
  amount: number | null
}

export interface FulfillmentScheduleDto {
  id: string
  saleId: string | null
  relatedSaleNumber: string | null
  method: FulfillmentMethod
  sequenceNumber: number
  scheduledDate: string // yyyy-MM-dd
  status: FulfillmentStatus
  createdAtUtc: string
  branchName: string
  recipientName: string
  /** null for pickups — a pickup transports nothing. */
  deliveryAddress: string | null
  contactNumber: string | null
  notes: string | null
  preparedByName: string
  completedAtUtc: string | null
  completedByName: string | null
  cancelledAtUtc: string | null
  cancelledByName: string | null
  cancellationReason: string | null
  cancellationDisposition: CancellationDisposition | null
  items: FulfillmentItemDto[]
  /** Read from the Sale; always 0 for a pickup. Never stored on the schedule. */
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

export interface CreateDeliveryReceiptRequest {
  scheduledDate: string
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  notes: string | null
  items: FulfillmentItemInput[]
}

export interface CreatePickupRequest {
  scheduledDate: string
  recipientName: string
  contactNumber: string | null
  notes: string | null
  items: FulfillmentItemInput[]
}

export interface CreateDeliveryReceiptBatchRequest {
  batchRequestId: string
  schedules: CreateDeliveryReceiptRequest[]
}

export interface CreatePickupBatchRequest {
  batchRequestId: string
  schedules: CreatePickupRequest[]
}

export interface FulfillmentBatchResultDto {
  created: FulfillmentScheduleDto[]
  wasExistingBatch: boolean
}

export interface PickupReplacementInput {
  scheduledDate: string
  recipientName: string
  contactNumber: string | null
  notes: string | null
}

export interface DeliveryReplacementInput {
  scheduledDate: string
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  notes: string | null
}

export interface CancelDeliveryRequest {
  reason: string
  disposition: CancellationDisposition
  replacement: PickupReplacementInput | null
}

export interface CancelPickupRequest {
  reason: string
  disposition: CancellationDisposition
  replacement: DeliveryReplacementInput | null
}

export interface CancellationResultDto {
  cancelled: FulfillmentScheduleDto
  replacement: FulfillmentScheduleDto | null
}

export type SaleFulfillmentStatus =
  | 'NotApplicable'
  | 'Fulfilled'
  | 'PartiallyFulfilled'
  | 'AwaitingDelivery'
  | 'AwaitingPickup'
  | 'AwaitingDeliveryAndPickup'
  | 'NeedsScheduling'
  | 'NeedsAttention'

/** The 8 buckets rendered per line on the Sale-detail page. All computed server-side. */
export interface SaleItemFulfillmentDto {
  saleItemId: string
  productName: string
  variantName: string | null
  quantity: number
  takeNowQuantity: number
  deliveryUnscheduledQuantity: number
  deliveryPendingQuantity: number
  deliveredQuantity: number
  pickupUnscheduledQuantity: number
  pickupPendingQuantity: number
  claimedQuantity: number
}

export interface FulfillmentConversionDto {
  id: string
  saleItemId: string
  productName: string
  variantName: string | null
  quantity: number
  fromMethod: FulfillmentMethod
  toMethod: FulfillmentMethod
  sourceRecordId: string | null
  replacementRecordId: string | null
  reason: string
  createdAtUtc: string
  createdByName: string
}

export interface SaleFulfillmentSummaryDto {
  saleId: string
  fulfillmentStatus: SaleFulfillmentStatus
  deliveryCharge: number
  items: SaleItemFulfillmentDto[]
  deliveries: FulfillmentScheduleDto[]
  pickups: FulfillmentScheduleDto[]
  conversions: FulfillmentConversionDto[]
  canCreateDelivery: boolean
  canCreatePickup: boolean
}
```

Then update the checkout types: add `pickupRequiredQuantity: number` beside every existing `deliveryRequiredQuantity` (the checkout item request, the cart line, and the sale-item DTO). Delete `DeliveryStatus`, `DeliveryReceiptDto`, `DeliveryReceiptItemDto`, `DeliveryReceiptSummaryDto`, `SaleDeliverySummaryDto`, `CancelDeliveryReceiptRequest`, `CreateDeliveryReceiptItemInput` and `DeliveryReceiptBatchResultDto` — every consumer is updated in Tasks 14-18. Leaving a compatibility alias behind is how two names for one concept take root; don't.

For the report types, add `PickupReportRowDto`, `PickupReportResultDto`, `FulfillmentReportRowDto`, `FulfillmentReportResultDto` and their params, mirroring the existing `DeliveryReport*` shapes against whatever Task 10 actually built. **Read Task 10's committed `ReportsContracts.cs` rather than guessing** — if the two disagree, the backend is the truth.

- [ ] **Step 2: Rewrite the API client as `api/fulfillment.ts`**

```ts
import { apiRequest } from './client'
import type {
  CancelDeliveryRequest,
  CancelPickupRequest,
  CancellationResultDto,
  CreateDeliveryReceiptBatchRequest,
  CreateDeliveryReceiptRequest,
  CreatePickupBatchRequest,
  CreatePickupRequest,
  FulfillmentBatchResultDto,
  FulfillmentScheduleDto,
  SaleFulfillmentSummaryDto,
} from './types'

/** Scheduled, partial, multi-schedule fulfillment across both methods. See the DTO block in
 * types.ts for the full backend-route contract. */
export const fulfillmentApi = {
  createDeliveryBatch: (saleId: string, body: CreateDeliveryReceiptBatchRequest) =>
    apiRequest<FulfillmentBatchResultDto>(`/api/sales/${saleId}/delivery-receipts/batch`, { method: 'POST', body }),

  createDelivery: (saleId: string, body: CreateDeliveryReceiptRequest) =>
    apiRequest<FulfillmentScheduleDto>(`/api/sales/${saleId}/delivery-receipts`, { method: 'POST', body }),

  createPickupBatch: (saleId: string, body: CreatePickupBatchRequest) =>
    apiRequest<FulfillmentBatchResultDto>(`/api/sales/${saleId}/pickups/batch`, { method: 'POST', body }),

  createPickup: (saleId: string, body: CreatePickupRequest) =>
    apiRequest<FulfillmentScheduleDto>(`/api/sales/${saleId}/pickups`, { method: 'POST', body }),

  listForSale: (saleId: string) =>
    apiRequest<FulfillmentScheduleDto[]>(`/api/sales/${saleId}/delivery-receipts`),

  getSaleSummary: (saleId: string) =>
    apiRequest<SaleFulfillmentSummaryDto>(`/api/sales/${saleId}/fulfillment`),

  get: (id: string) => apiRequest<FulfillmentScheduleDto>(`/api/delivery-receipts/${id}`),

  markDelivered: (id: string) =>
    apiRequest<FulfillmentScheduleDto>(`/api/delivery-receipts/${id}/deliver`, { method: 'POST' }),

  markClaimed: (id: string) =>
    apiRequest<FulfillmentScheduleDto>(`/api/delivery-receipts/${id}/claim`, { method: 'POST' }),

  cancelDelivery: (id: string, body: CancelDeliveryRequest) =>
    apiRequest<CancellationResultDto>(`/api/delivery-receipts/${id}/cancel`, { method: 'POST', body }),

  cancelPickup: (id: string, body: CancelPickupRequest) =>
    apiRequest<CancellationResultDto>(`/api/pickups/${id}/cancel`, { method: 'POST', body }),
}
```

Add the two new report calls to `api/reports.ts` beside the existing delivery one.

- [ ] **Step 3: Method-aware labels in `lib/pos.ts`**

This is the spec's label table, and it is the single place the UI decides what a status is *called*. Replace `DELIVERY_STATUS_LABELS` and `deliveryStatusTone` with:

```ts
/** The spec's label table. A status never renders on its own — it always renders through its
 * method, because "Completed" means "Delivered" for a delivery and "Claimed" for a pickup. */
export function fulfillmentStatusLabel(method: FulfillmentMethod, status: FulfillmentStatus): string {
  if (method === 'TakeNow') return status === 'Completed' ? 'Taken now' : 'Take now'
  if (method === 'Delivery') {
    switch (status) {
      case 'Unscheduled': return 'For delivery (unscheduled)'
      case 'Pending': return 'Scheduled for delivery'
      case 'Completed': return 'Delivered'
      case 'Cancelled': return 'Delivery cancelled'
    }
  }
  switch (status) {
    case 'Unscheduled': return 'For pickup (unscheduled)'
    case 'Pending': return 'Scheduled for pickup'
    case 'Completed': return 'Claimed'
    case 'Cancelled': return 'Pickup cancelled'
  }
}

export function fulfillmentStatusTone(status: FulfillmentStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (status) {
    case 'Completed': return 'success'
    case 'Pending': return 'warning'
    case 'Cancelled': return 'danger'
    default: return 'neutral'
  }
}

export const FULFILLMENT_METHOD_LABELS: Record<FulfillmentMethod, string> = {
  TakeNow: 'Take now',
  Delivery: 'Delivery',
  Pickup: 'Pickup',
}

export const CANCELLATION_DISPOSITION_LABELS: Record<CancellationDisposition, string> = {
  DeliverLater: 'Deliver later (reschedule)',
  PickupLater: 'Pick up later (reschedule)',
  ConvertToDelivery: 'Convert to delivery',
  ConvertToPickup: 'Convert to pickup',
  CustomerPickedUpInstead: 'Customer picked it up instead',
}
```

`SALE_FULFILLMENT_STATUS_LABELS` and `saleFulfillmentStatusTone` are **fully replaced**, not extended — Task 5 replaced the backend enum's members wholesale, so every key in these two maps changes:

```ts
export const SALE_FULFILLMENT_STATUS_LABELS: Record<SaleFulfillmentStatus, string> = {
  NotApplicable: 'No fulfillment needed',
  Fulfilled: 'Fulfilled',
  PartiallyFulfilled: 'Partially fulfilled',
  AwaitingDelivery: 'Awaiting delivery',
  AwaitingPickup: 'Awaiting pickup',
  AwaitingDeliveryAndPickup: 'Awaiting delivery and pickup',
  NeedsScheduling: 'Needs scheduling',
  NeedsAttention: 'Needs attention',
}

export function saleFulfillmentStatusTone(s: SaleFulfillmentStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (s) {
    case 'Fulfilled': return 'success'
    case 'NeedsAttention': return 'danger'
    case 'NotApplicable': return 'neutral'
    default: return 'warning'
  }
}
```

**Leave `DeliveryReportPreset` alone.** Its `'NeedsRescheduling'` and `'Delivered'` members look like the old status names but are report *filter presets* — a different type with a different lifetime. Task 18 adds pickup presets to it; this task does not touch it.

**Note the omission:** there is deliberately no label for TakeNow + Cancelled. TakeNow is a checkout-time allocation, not a schedule — it has no lifecycle and can never be cancelled. If a later task finds itself needing that label, the design has gone wrong somewhere; report it rather than adding one.

- [ ] **Step 4: `FulfillmentStatusBadge.tsx`**

```tsx
import type { FulfillmentMethod, FulfillmentStatus } from '../../api/types'
import { fulfillmentStatusLabel, fulfillmentStatusTone } from '../../lib/pos'
import { Badge } from '../ui'

export function FulfillmentStatusBadge({
  method,
  status,
}: {
  method: FulfillmentMethod
  status: FulfillmentStatus
}) {
  return <Badge tone={fulfillmentStatusTone(status)}>{fulfillmentStatusLabel(method, status)}</Badge>
}
```

- [ ] **Step 5: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: **FAILS**, with errors confined to the consumers Tasks 14-18 rewrite (`PosPage`, `CartItem`, `usePosCart`, `DeliveryDetailsFields`, `SaleDetailPage`, `CreateDeliveryReceiptModal`, `CancelDeliveryModal`, `DeliveryReceiptPage`, `DeliveryReportsPage`). List the failing files in your report — that list is the checklist for the tasks that follow. An error anywhere else means a type is wrong here.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): fulfillment types, API client and method-aware status labels"
```

---

## Task 14: POS — three-way allocation at checkout

**Files:**
- Modify: `web/negosio-web/src/components/pos/CartItem.tsx`
- Modify: `web/negosio-web/src/components/pos/DeliveryDetailsFields.tsx` → rename to `FulfillmentDetailsFields.tsx`
- Modify: `web/negosio-web/src/hooks/usePosCart.ts`
- Modify: `web/negosio-web/src/pages/PosPage.tsx`
- Modify: `web/negosio-web/src/lib/posStorage.ts` (cart persistence shape)

**Interfaces:**
- Consumes: Task 13's types; the checkout contract from Task 9.
- Produces: checkout bodies carrying `pickupRequiredQuantity`, and a follow-up pickup batch post-checkout.

The POS already does per-item delivery allocation and posts a delivery batch after checkout. This task makes that three-way.

- [ ] **Step 1: Cart line shape**

Add `pickupRequiredQuantity: number` beside the existing `deliveryRequiredQuantity` in the cart-line type, in `usePosCart`'s reducer/setters, and in the persisted cart shape in `posStorage.ts`.

**Persisted-cart migration:** an in-flight cart saved before this deploy has no `pickupRequiredQuantity` key. On load, default a missing value to `0` rather than letting `undefined` reach the checkout body — an `undefined` there serializes to a missing key, which the backend defaults to 0 anyway, but `undefined` in arithmetic yields `NaN` and will corrupt the take-now display. Coerce on read.

- [ ] **Step 2: Per-line three-way allocation UI**

In `CartItem.tsx`, the line currently shows quantity and a delivery-quantity input. It now shows the allocation as three numbers that must sum to the line quantity:

```
Qty 10      Take now 3   ·   Delivery [ 4 ]   ·   Pickup [ 3 ]
```

- Delivery and pickup are **inputs**; take-now is **derived and read-only** (`quantity - delivery - pickup`). Never make all three editable — that creates a three-way constraint with no clear resolution when the user edits one.
- Both inputs use the existing `qtyDraft`/`commitQty` draft-on-blur pattern already in this file. Do not introduce a second pattern.
- On commit, clamp so `delivery + pickup <= quantity`: if the new value would overflow, reduce it to the maximum that fits and show the clamped value in the input. Do not silently reduce the *other* input — the user did not touch it.
- If the line quantity is reduced below `delivery + pickup`, reduce pickup first, then delivery, until the identity holds. Do this in `usePosCart` where quantity changes, not in the component.
- Show take-now in a muted tone with the label "Take now", so the identity `Sold = TakeNow + Delivery + Pickup` is visible at all times.

- [ ] **Step 3: `FulfillmentDetailsFields.tsx`**

`DeliveryDetailsFields` currently collects one delivery schedule's recipient/address/date/notes and supports multiple schedules. Generalize it:

- It takes a `method: FulfillmentMethod` prop and hides the address field entirely when `method === 'Pickup'`. Do not render a disabled or empty address input for pickups — the field does not exist for that method.
- The heading and the add-button text come from `FULFILLMENT_METHOD_LABELS` ("Add another delivery" / "Add another pickup").
- The per-schedule item allocation validates against that method's remaining quantity only.

`PosPage` renders it twice — once for delivery when any line has `deliveryRequiredQuantity > 0`, once for pickup when any line has `pickupRequiredQuantity > 0` — each with its own schedule list.

- [ ] **Step 4: Post-checkout submission**

Checkout returns the created `saleItemId`s. The page then posts **up to two** batches, each with its own fresh `batchRequestId` (they are independent idempotency keys — never share one between the delivery and pickup batch):

1. `fulfillmentApi.createDeliveryBatch` if there are delivery schedules
2. `fulfillmentApi.createPickupBatch` if there are pickup schedules

**Carry forward the fix from the delivery work:** before submitting, reconcile the schedule state against the *current* cart lines. If a line was edited or removed after its schedule was built, drop or clamp the affected schedule entries rather than posting stale `saleItemId`/quantity pairs. Posting a stale batch fails the whole batch and loses schedules for a sale that is already paid — this was a real bug found in review, and it now has two batches to go wrong in instead of one.

If a batch post fails, the sale still succeeded. Show a non-blocking error naming the sale number and telling the user they can schedule from the sale's detail page — never a modal that implies the payment failed.

- [ ] **Step 5: Type-check**

Run: `cd web/negosio-web && npx tsc -b`
Expected: POS files clean; remaining errors only in the Sale-detail / print / reports files that Tasks 15-18 own.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): POS three-way take-now / delivery / pickup allocation"
```

---

## Task 15: Sale detail — the 8-row breakdown, both schedule lists, and conversion history

**Files:**
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`
- Modify: `web/negosio-web/src/components/sales/CreateDeliveryReceiptModal.tsx` → generalize to `CreateFulfillmentScheduleModal.tsx`
- Create: `web/negosio-web/src/components/sales/FulfillmentBreakdownTable.tsx`
- Create: `web/negosio-web/src/components/sales/ConversionHistoryList.tsx`

**Interfaces:**
- Consumes: `SaleFulfillmentSummaryDto` (Task 13), `fulfillmentApi.getSaleSummary`.
- Produces: the surface the live UI verification in Task 19 exercises.

- [ ] **Step 1: `FulfillmentBreakdownTable.tsx`**

Per the spec, each sale line shows **8 rows**. Render them in this fixed order, and **omit a row when its quantity is 0** — a line that was entirely taken now should not display six zeroes:

| Row | Source field | Label |
|---|---|---|
| 1 | `quantity` | Sold |
| 2 | `takeNowQuantity` | Taken now |
| 3 | `deliveryUnscheduledQuantity` | For delivery (unscheduled) |
| 4 | `deliveryPendingQuantity` | Scheduled for delivery |
| 5 | `deliveredQuantity` | Delivered |
| 6 | `pickupUnscheduledQuantity` | For pickup (unscheduled) |
| 7 | `pickupPendingQuantity` | Scheduled for pickup |
| 8 | `claimedQuantity` | Claimed |

Row 1 always renders. **Every label comes from `fulfillmentStatusLabel`, not from a string typed into this component** — one label table, one place to change it.

Do **not** compute any of these in the component. They arrive from the server; recomputing them here creates the second source of truth the spec forbids.

- [ ] **Step 2: Two schedule sections**

Render `summary.deliveries` and `summary.pickups` as two separate labelled sections ("Deliveries", "Pickups"), each row showing `Delivery {sequenceNumber}` / `Pickup {sequenceNumber}`, the scheduled date, a `FulfillmentStatusBadge`, and the actions permitted for that row:

| Row state | Actions |
|---|---|
| Pending delivery | Mark delivered · Cancel · Print |
| Pending pickup | Mark claimed · Cancel · Print |
| Completed (either) | Print only |
| Cancelled (either) | Print only, plus the disposition shown as text |

Cancel is gated on `useCan` for the management roles (the `FulfillmentCancel` policy). "Mark delivered" never appears on a pickup row and "Mark claimed" never appears on a delivery row — the backend rejects the mismatch, and the UI should not offer it in the first place.

A cancelled row shows `cancellationReason` and, when present, `CANCELLATION_DISPOSITION_LABELS[cancellationDisposition]`. Rows cancelled before this feature existed have a null disposition — render "—", not a guess.

Each mutation (`markDelivered`, `markClaimed`) needs `onError` **and** `invalidateQueries` for both the sale and the fulfillment summary. A missing `onError` was a real review finding in the delivery work.

- [ ] **Step 3: Create buttons**

Two buttons, each gated on the server's flag — `canCreateDelivery` and `canCreatePickup`. When a flag is false, hide the button and show the explanatory text the spec gives ("All delivery items have already been scheduled or delivered." / "All pickup items have already been scheduled or claimed."). Do not disable-with-tooltip; the spec asks for the sentence.

`CreateFulfillmentScheduleModal` takes a `method` prop, hides the address field for pickup, and validates each line against that method's *unscheduled* quantity from the summary.

**The `useEffect` prefill must depend on `[open]` alone.** See the frontend preamble.

- [ ] **Step 4: `ConversionHistoryList.tsx`**

Renders `summary.conversions`, newest first, one line each:

> **3 × Coke 1.5L** — Delivery → Pickup · "Customer came to the store" · Maria Santos · 13 Sep 2026, 2:14 PM

Link `sourceRecordId` and `replacementRecordId` to their print pages when non-null. If the list is empty, render nothing at all — no empty-state card for a section most sales will never have.

- [ ] **Step 5: Type-check**

Run: `cd web/negosio-web && npx tsc -b`

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(web): sale-detail fulfillment breakdown, pickup schedules and conversion history"
```

---

## Task 16: Cancellation and disposition modals

**Files:**
- Modify: `web/negosio-web/src/components/sales/CancelDeliveryModal.tsx` → `CancelFulfillmentModal.tsx`

**Interfaces:**
- Consumes: `CancelDeliveryRequest` / `CancelPickupRequest` (Task 13).
- Produces: the UI for the spec's central workflow. Task 19 verifies it live.

This is the feature's most user-visible decision point and where a wrong option list would produce exactly the bad data the spec is written to prevent.

- [ ] **Step 1: One modal, method-driven options**

The modal takes the `FulfillmentScheduleDto` being cancelled. Its disposition options come from the record's `method` — **never a fixed list**:

**Cancelling a Delivery:**
- *Deliver later (reschedule)* — the items go back to unscheduled for delivery.
- *Convert to pickup* — the customer will collect them instead. Requires pickup details.
- *Customer picked it up instead* — they already collected them. Requires pickup details; creates an already-claimed pickup.

**Cancelling a Pickup:**
- *Pick up later (reschedule)* — back to unscheduled for pickup.
- *Convert to delivery* — requires delivery details (including address).

**There is no "Take now" option on either list, and there never will be.** TakeNow is a checkout-time allocation. If someone collects goods after the sale, that is a Pickup that is already Claimed — which is exactly what *Customer picked it up instead* records. An implementer who adds a take-now disposition has misread the spec; the backend will reject it, but the option should not exist in the UI either.

- [ ] **Step 2: Conditional replacement sub-form**

Selecting a disposition that needs a replacement reveals its fields inline (recipient, contact, notes, date — plus address for delivery). Selecting a reschedule-only disposition hides them and sends `replacement: null`.

Date rules, matching the backend:
- *Convert to pickup* / *Convert to delivery* — future schedule; default to today, reject a past date client-side with the same message the server uses.
- *Customer picked it up instead* — **this already happened.** Default to today and **allow a past date**. Label the field "Date collected", not "Scheduled date". Do not apply the not-in-the-past rule here; the backend deliberately does not either.

Prefill recipient/contact from the record being cancelled — the same customer, in every realistic case.

- [ ] **Step 3: Confirmation copy that says what will happen**

Before submitting, show a one-line summary of the outcome, because these actions create records:

- *Deliver later*: "Delivery 1 will be cancelled. 3 × Coke 1.5L returns to unscheduled."
- *Convert to pickup*: "Delivery 1 will be cancelled and Pickup 2 created for 15 Sep 2026."
- *Customer picked it up instead*: "Delivery 1 will be cancelled and recorded as **Pickup 2 — Claimed** on 13 Sep 2026."
- *Convert to delivery*: "Pickup 1 will be cancelled and Delivery 2 created for 15 Sep 2026."

The reason field is required (the backend requires it); the submit button stays disabled until it has content.

- [ ] **Step 4: Handle the result**

The response is a `CancellationResultDto` with both records. On success, invalidate the sale and fulfillment queries, and when `replacement` is non-null show a toast naming it ("Pickup 2 created"). `onError` is required.

- [ ] **Step 5: Type-check + commit**

Run: `cd web/negosio-web && npx tsc -b`

```bash
git add -A
git commit -m "feat(web): method-aware cancellation and conversion modal"
```

---

## Task 17: Print page — one page, both methods

**Files:**
- Modify: `web/negosio-web/src/pages/DeliveryReceiptPage.tsx`
- Modify: `web/negosio-web/src/components/receipt/deliveryReceiptStyles.ts`
- Modify: `web/negosio-web/src/App.tsx` (route)

**Interfaces:** consumes `fulfillmentApi.get` (Task 13), which serves both methods.

One route and one component render both, driven by `dto.method`. A parallel pickup page would duplicate the entire receipt-settings rendering for a document that differs by two lines.

- [ ] **Step 1: Method-driven differences**

| Element | Delivery | Pickup |
|---|---|---|
| Document title | "Delivery Receipt" | "Pickup Slip" |
| Sequence label | "Delivery {n}" | "Pickup {n}" |
| Date label | "Scheduled delivery date" | "Scheduled pickup date" |
| Address block | rendered | **omitted entirely** |
| Delivery charge line | rendered when non-zero and `showPrices` | **never rendered** |
| Signature block | "Received by" | "Claimed by" |
| Status stamp | via `fulfillmentStatusLabel` | via `fulfillmentStatusLabel` |

Receipt settings (`headerText`, `footerText`, `showPrices`, `showSignatureFields`, …) apply identically to both — they come from the same DTO and the same tenant settings. Do not add pickup-specific settings; the spec does not ask for them and it would double the settings surface.

- [ ] **Step 2: Keep the route path**

Keep `/delivery-receipts/:id` as the path even for pickups. The id is the identity, links to it already exist in printed and saved pages, and changing it would break them for no user-visible gain. Add a redirect from `/pickups/:id` to it if you want the friendlier URL to work — but the canonical one does not move.

- [ ] **Step 3: Type-check + commit**

Run: `cd web/negosio-web && npx tsc -b`

```bash
git add -A
git commit -m "feat(web): method-aware fulfillment print page"
```

---

## Task 18: Reports — three views behind one page

**Files:**
- Modify: `web/negosio-web/src/pages/DeliveryReportsPage.tsx` → rename to `FulfillmentReportsPage.tsx`
- Modify: `web/negosio-web/src/App.tsx` (route), and the nav entry that points at it

**Interfaces:** consumes the three report endpoints from Task 10 and their types from Task 13.

- [ ] **Step 1: Three tabs, one filter bar**

The page keeps the existing `ReportFilterBar` and `useReportFilters` URL-sync, and adds a tab switch: **Deliveries · Pickups · All fulfillment**. The active tab lives in the URL alongside the existing filters, so a filtered view is still shareable and a refresh lands where the user was.

Columns per tab:

- **Deliveries** — unchanged from today. This tab is a regression surface: if anything about it looks different after this task, that is a bug, not an improvement.
- **Pickups** — same columns minus address and delivery charge.
- **All fulfillment** — one row per sale: sale number, date, customer, sold qty, taken now, delivered, claimed, pending (delivery), pending (pickup), overall status badge, delivery charge.

- [ ] **Step 2: Totals**

Each tab shows the totals its endpoint returns. **Never sum a column client-side to produce a delivery-charge total** — that reintroduces, in the browser, the exact multiplication bug the backend fixed by aggregating over distinct sales. The server has already done it correctly; render what it sends.

- [ ] **Step 3: Status filter is method-aware**

The status dropdown offers the labels for the tab's method via `fulfillmentStatusLabel` — "Delivered" on the delivery tab, "Claimed" on the pickup tab, for the same underlying `Completed` value. The combined tab filters on `SaleFulfillmentStatus` instead.

- [ ] **Step 4: Type-check + build**

Run: `cd web/negosio-web && npx tsc -b && npm run build`
Expected: both clean, **zero errors.** This is the last frontend task; nothing may remain broken.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(web): delivery, pickup and combined fulfillment report views"
```

---

## Task 19: Final verification — full suite, clean build, and the live UI walkthrough

**Files:** none unless a defect is found.

- [ ] **Step 1: Backend**

Run: `dotnet test`
Expected: everything green. Record the exact count and compare it against the count from Task 12.

- [ ] **Step 2: Frontend**

Run: `cd web/negosio-web && npx tsc -b && npm run build`
Expected: zero errors from both.

- [ ] **Step 3: Seed a live scenario**

A seed script for the delivery feature already exists in this session's scratchpad (`seed-test.mjs`) — it registers a tenant, seeds products and stock, opens a register session, checks out a sale with delivery allocation and a ₱100 delivery charge, and creates two schedules. Extend it to also allocate pickup quantity and create a pickup schedule, then run it against a locally running API.

- [ ] **Step 4: Walk the spec's UI verification scenarios**

Open `docs/superpowers/specs/2026-09-13-pickup-fulfillment.md`, find the numbered UI verification list, and perform **each** scenario in the running app. For each one record: what you did, what you saw, and pass/fail. Capture a screenshot for each and **look at it** — a scenario is not verified because the API returned 200; it is verified because the correct words appeared on screen.

The memory note `visual-verify-headless-chrome.md` has the working headless-Chrome + CDP recipe for this machine, including the `MSYS_NO_PATHCONV` gotcha.

Pay particular attention to the two the spec is most emphatic about:
- **"Customer picked up instead"** must leave a **Cancelled** delivery and a **Claimed** pickup on screen. If the delivery shows "Delivered", or anything anywhere says "Taken now", that is a failure — report it, do not explain it away.
- **The delivery charge appears exactly once** on a sale with multiple schedules across both methods, and is **unchanged** by a conversion. Note the figure before and after a conversion and compare them literally.

- [ ] **Step 5: Confirm nothing was merged or pushed**

Run: `git status -sb` and `git log --oneline origin/master..HEAD 2>/dev/null | wc -l`
Confirm the branch is still `feature/pos-for-delivery`, that nothing was pushed, and that `master` is untouched. **The user asked for this twice. Do not merge. Do not push.**

- [ ] **Step 6: Write the final report**

The spec specifies a required final report format. Follow it exactly, and include:
- the chosen architecture and why (from this plan's header)
- the full test count, before and after
- the spec's 40-item test table with its coverage mapping
- the UI verification results, scenario by scenario
- anything deliberately not done, and why

Report honestly. A failing scenario reported plainly is worth more than a green summary that does not survive the user's own testing.
