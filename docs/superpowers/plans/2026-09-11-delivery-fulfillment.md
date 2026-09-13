# Delivery Fulfillment (Scheduled & Partial Deliveries) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the existing one-DR-per-sale delivery feature into a full fulfillment system: a sale's sold quantities split into Take-now vs. delivery-required, delivery-required quantities scheduled across one or more dated, partial `DeliveryReceipt` records with Pending/Delivered/Cancelled status, plus reporting across both the delivery-schedule and sale-fulfillment views.

**Architecture:** Extends, never replaces, the existing `DeliveryReceipt`/`DeliveryReceiptItem` entities and the existing `IDeliveryReceiptService`. `DeliveryReceipt`→`Sale` was already schema-wise many-to-one (the one-per-sale rule was service-only, not a DB constraint) — no relationship migration needed, just new columns, a real `DeliveryReceiptItem.SaleItemId` FK (today it's a disconnected print snapshot), and a status/audit lifecycle. `SaleItem` gains a persisted `DeliveryRequiredQuantity` (set once, at checkout, following the exact `ReturnedQuantity`/`ReturnableQuantity` stored-total/computed-remainder pattern already used for returns). Concurrency uses this codebase's standard idiom — EF optimistic `RowVersion` + `DbUpdateConcurrencyException` → rollback → `ConflictException` (409) — not the pessimistic-lock exception used by Void (that lock exists specifically for a cross-aggregate race delivery status doesn't have).

**Tech Stack:** .NET 9 / EF Core 9 / SQL Server (LocalDB), FluentValidation, xUnit + FluentAssertions on the backend; React 19 / TypeScript (strict) / Vite / TanStack Query on the frontend (no frontend test runner in this repo — verify with `npx tsc -b`, not a bare `--noEmit`, which silently checks zero files under this repo's TS project-references setup).

**Spec:** The feature goal, approved business rules, data model, API shape, migration/legacy-data handling, and required-test list were given directly by the project owner in conversation (no separate spec file) — this plan implements that spec verbatim except where noted as an explicit, reasoned adaptation to the real codebase (each such deviation is called out inline in the task it affects). Prior delivery-charge work (already merged) lives in `docs/superpowers/plans/2026-09-11-pos-delivery-charge.md` and must keep working unchanged: `Sales.DeliveryCharge` stays the sole financial source of truth, applied once per sale regardless of schedule count.

## Global Constraints

- **`DeliveryRequiredQuantity` is set once, at checkout, and never mutated afterward.** No public setter, no "edit allocation" endpoint. `TakeNowQuantity = Quantity − DeliveryRequiredQuantity` is a computed property, never persisted (mirrors `SaleItem.ReturnableQuantity`'s existing computed-property pattern exactly).
- **`Sales.DeliveryCharge` is never touched by this plan's own logic** beyond being read (never written, never duplicated onto `DeliveryReceipt`/`DeliveryReceiptItem`, never summed per-schedule). Reports that need it must aggregate by **distinct Sale**, not by joined `DeliveryReceipt` row — multiplying it by schedule count is the one correctness bug this plan must never introduce (and must fix in the existing `GetDeliveriesAsync`, which assumed one-DR-per-sale and would double-count once that stops being true).
- **Every status transition is enforced server-side**, in the domain entity, throwing `InvalidOperationException` on an invalid transition (matches `RegisterSession.Close`'s and `SaleItem.RecordReturn`'s existing idiom) — the calling service pre-checks with a typed `BusinessRuleException`/`ErrorCodes` value first, so the domain-level throw is defense-in-depth, normally unreachable.
- **Concurrency:** `DeliveryReceipt` gets a `RowVersion` (`byte[]`, `IsRowVersion()`, mirrors `Sale.RowVersion`). Status-changing operations (mark-delivered, cancel — both UPDATEs on an existing row) catch `DbUpdateConcurrencyException`, roll back explicitly (`await transaction.RollbackAsync(...)` **before** any re-read — matches `ReturnService`'s and `InventoryService`'s documented convention exactly), and throw `ConflictException` (409) with a new `ErrorCodes.DeliveryReceiptConcurrencyConflict`. Aggregate over-allocation (two users scheduling the same unscheduled quantity — two concurrent **INSERTs**, not competing updates of one row, so RowVersion structurally cannot catch it) is guarded by a pessimistic `WITH (UPDLOCK, HOLDLOCK)` lock on the sale's `SaleItems` rows, taken immediately after the transaction begins and held until commit/rollback — the one pessimistic-lock idiom already in this codebase (`VoidSaleService`), reused here because it is the right tool exactly where RowVersion cannot apply. Availability is (re-)computed only after this lock is held.
- **Idempotency:** batch delivery creation takes a client-supplied `BatchRequestId` (`Guid`), stored on every `DeliveryReceipt` row the batch creates, backed by a **filtered unique index** `(TenantId, BatchRequestId) WHERE BatchRequestId IS NOT NULL`. Pattern: check-first (return the existing batch's rows if found) then catch a unique-violation on insert as the race backstop — the exact idiom `CheckoutService` already uses for `ClientRequestId`, via `SqlUniqueViolation.TryGetConstraintName`.
- **HTTP verbs:** every state-changing action is `POST /{id}/{verb}` — this codebase never uses `PATCH`, and reserves `PUT` for pure field-replacement (confirmed across `SalesController.Void`, `RegisterSessionsController.Close`/`ForceClose`, `StaffController.Deactivate`/`Reactivate`). Do not introduce `PATCH`.
- **Business "today"** is computed via `ReportPeriodResolver.BusinessOffset` (a `public static readonly TimeSpan` = UTC+8, already the whole codebase's fixed-offset convention — do not re-declare a second copy of this constant). Reject a `ScheduledDeliveryDate` earlier than business-local today; store `DeliveredAtUtc`/`CancelledAtUtc` in UTC; the frontend has zero timezone awareness today (day-bounds are built from the browser's own local `Date` parsing) — this plan does not change that convention, only reuses it for the new date field the same way `SalesPage.tsx`/`DeliveryReportsPage.tsx` already build day-bounds.
- **`ScheduledDeliveryDate` is the first persisted `DateOnly` column in this codebase.** EF Core 9 maps `DateOnly` → SQL `date` by convention with no explicit `HasColumnType` call needed — confirm the generated migration produces `type: "date"` and do not hand-override it unless it doesn't.
- **Legacy data backfill is exact, not approximate**, because checkout already merges duplicate-variant lines into one `SaleItem` per variant per sale (confirmed in `CheckoutService.CheckoutAsync`) — so matching an existing `DeliveryReceiptItem` (which has no `SaleItemId` today) to its real `SaleItem` by `(SaleId, ProductNameSnapshot, VariantNameSnapshot)` is unambiguous: both sides are immutable point-in-time snapshots from the same sale event, and a sale can have at most one `SaleItem` per matching name pair. Add the new `SaleItemId` column nullable, backfill via a hand-written `UPDATE ... FROM` in the same migration, **then** `AlterColumn` to `NOT NULL` — this ordering makes the migration self-verifying: it fails loudly if any row doesn't match, rather than silently leaving corrupt data.
- **Do not add a global Delivery Receipt number.** `SequenceNumber` is `int`, scoped to `(SaleId, SequenceNumber)` uniqueness, displayed as "Delivery {N}" — never a tenant-wide or cross-sale document number.
- **A partial delivery prints only its own assigned items** — never the whole sale's item list. The existing `GetAsync`'s `dr.Items` navigation already scopes to one `DeliveryReceipt`'s own items; this falls out for free once `DeliveryReceiptItem` carries real per-schedule quantities instead of always being the whole-sale snapshot.
- Do not merge, do not push — stay on the current feature branch. Commit after every green step.

---

## Backend

### Task 1: `DeliveryStatus` enum + `SaleItem.DeliveryRequiredQuantity`

**Files:**
- Create: `src/Negosio.Domain/Enums/DeliveryStatus.cs`
- Modify: `src/Negosio.Domain/Entities/Sales/SaleItem.cs`
- Modify: `src/Negosio.Domain/Entities/Sales/Sale.cs` (`AddItem` signature only)
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/SaleItemConfiguration.cs`
- Modify: `tests/Negosio.UnitTests/Sales/SaleVoidTests.cs`, `tests/Negosio.UnitTests/Pos/RegisterSessionTests.cs` (both call `sale.AddItem(...)` positionally — the new trailing optional parameter needs no call-site change since it defaults to `0m`, but confirm this by reading both files first)
- Test: `tests/Negosio.UnitTests/Sales/SaleItemFulfillmentTests.cs` (new)

**Interfaces:**
- Produces: `SaleItem.DeliveryRequiredQuantity` (`decimal`, private-set, default `0m`), `SaleItem.TakeNowQuantity` (computed `Quantity - DeliveryRequiredQuantity`), `Sale.AddItem(...)`'s new optional trailing `decimal deliveryRequiredQuantity = 0m` parameter — Task 7 (checkout) is the only production caller that will ever pass a non-default value.
- Produces: `Negosio.Domain.Enums.DeliveryStatus { Pending = 1, Delivered = 2, Cancelled = 3 }` — matches this codebase's 1-based enum convention (`SaleStatus`, `RegisterSessionStatus`). Task 2 consumes this for `DeliveryReceipt.Status`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Negosio.UnitTests/Sales/SaleItemFulfillmentTests.cs`:

```csharp
using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Sales;

public class SaleItemFulfillmentTests
{
    private static Sale MakeSale() =>
        Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public void AddItem_defaults_delivery_required_quantity_to_zero()
    {
        var sale = MakeSale();

        var item = sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m);

        item.DeliveryRequiredQuantity.Should().Be(0m);
        item.TakeNowQuantity.Should().Be(10m);
    }

    [Fact]
    public void AddItem_accepts_a_partial_delivery_requirement()
    {
        var sale = MakeSale();

        var item = sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m,
            deliveryRequiredQuantity: 8m);

        item.DeliveryRequiredQuantity.Should().Be(8m);
        item.TakeNowQuantity.Should().Be(2m);
    }

    [Fact]
    public void AddItem_rejects_a_negative_delivery_required_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m,
            deliveryRequiredQuantity: -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddItem_rejects_a_delivery_required_quantity_exceeding_sold_quantity()
    {
        var sale = MakeSale();

        var act = () => sale.AddItem(Guid.NewGuid(), "Coke", null, "SKU", null, 75m, 10m, DiscountType.None, 0m, 750m, 0m, 0m, 750m, 40m,
            deliveryRequiredQuantity: 10.5m);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.UnitTests --filter SaleItemFulfillmentTests`
Expected: FAIL to compile — `AddItem` has no `deliveryRequiredQuantity` parameter, `SaleItem` has no `DeliveryRequiredQuantity`/`TakeNowQuantity` members.

- [ ] **Step 3: Create the enum**

`src/Negosio.Domain/Enums/DeliveryStatus.cs`:

```csharp
namespace Negosio.Domain.Enums;

/// <summary>
/// Lifecycle of one delivery schedule (a <see cref="Entities.DeliveryReceipt"/>). Persisted
/// numerically; values must stay stable. Pending → Delivered or Pending → Cancelled are the only
/// transitions; both Delivered and Cancelled are final. A cancelled schedule is never reactivated —
/// its released quantities are picked up by a brand-new Pending record instead.
/// </summary>
public enum DeliveryStatus
{
    Pending = 1,
    Delivered = 2,
    Cancelled = 3
}
```

- [ ] **Step 4: Extend `SaleItem`**

In `src/Negosio.Domain/Entities/Sales/SaleItem.cs`, add the parameter to the `internal` constructor (after `costPriceSnapshot`, before the body sets `ReturnedQuantity`):

```csharp
    internal SaleItem(
        Guid tenantId,
        Guid saleId,
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        string? skuSnapshot,
        string? barcodeSnapshot,
        decimal unitPrice,
        decimal quantity,
        DiscountType discountKind,
        decimal discountValue,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal? costPriceSnapshot,
        decimal deliveryRequiredQuantity)
    {
        TenantId = tenantId;
        SaleId = saleId;
        ProductVariantId = productVariantId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        SkuSnapshot = skuSnapshot;
        BarcodeSnapshot = barcodeSnapshot;
        UnitPrice = unitPrice;
        Quantity = quantity;
        DiscountKind = discountKind;
        DiscountValue = discountValue;
        GrossAmount = grossAmount;
        DiscountAmount = discountAmount;
        TaxAmount = taxAmount;
        NetAmount = netAmount;
        CostPriceSnapshot = costPriceSnapshot;
        ReturnedQuantity = 0m;
        DeliveryRequiredQuantity = deliveryRequiredQuantity;
    }
```

Add the two new members after `ReturnableQuantity` (line 90):

```csharp
    public decimal ReturnableQuantity => Quantity - ReturnedQuantity;

    /// <summary>Set once, at checkout, from the client's requested delivery quantity for this line —
    /// never mutated afterward. A future correction (e.g. the cashier mis-split delivery vs. take-now)
    /// must go through a deliberate adjustment workflow, not a setter on this property; none exists
    /// today, matching the plan's explicit "no silent change after checkout" constraint.</summary>
    public decimal DeliveryRequiredQuantity { get; private set; }

    /// <summary>Computed, never persisted — the portion of this line the customer takes at checkout.</summary>
    public decimal TakeNowQuantity => Quantity - DeliveryRequiredQuantity;
```

- [ ] **Step 5: Extend `Sale.AddItem`**

In `src/Negosio.Domain/Entities/Sales/Sale.cs`, replace `AddItem` (the whole method):

```csharp
    public SaleItem AddItem(
        Guid productVariantId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        string? skuSnapshot,
        string? barcodeSnapshot,
        decimal unitPrice,
        decimal quantity,
        DiscountType discountKind,
        decimal discountValue,
        decimal grossAmount,
        decimal discountAmount,
        decimal taxAmount,
        decimal netAmount,
        decimal? costPriceSnapshot,
        decimal deliveryRequiredQuantity = 0m)
    {
        if (quantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be greater than zero.");
        }

        if (deliveryRequiredQuantity < 0m || deliveryRequiredQuantity > quantity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deliveryRequiredQuantity), "Delivery-required quantity must be between 0 and the sold quantity.");
        }

        var item = new SaleItem(
            TenantId, Id, productVariantId, productNameSnapshot, variantNameSnapshot, skuSnapshot,
            barcodeSnapshot, unitPrice, quantity, discountKind, discountValue, grossAmount,
            discountAmount, taxAmount, netAmount, costPriceSnapshot, deliveryRequiredQuantity);
        _items.Add(item);
        return item;
    }
```

- [ ] **Step 6: EF configuration**

In `src/Negosio.Infrastructure/Persistence/Configurations/SaleItemConfiguration.cs`, add right after the `ReturnedQuantity` line:

```csharp
        builder.Property(i => i.ReturnedQuantity).HasPrecision(18, 3);
        builder.Property(i => i.DeliveryRequiredQuantity).HasPrecision(18, 3).HasDefaultValue(0m);
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.UnitTests --filter "SaleItemFulfillmentTests|SaleVoidTests|SaleAggregateTests"`
Expected: PASS — the four new tests plus the two pre-existing files that call `sale.AddItem(...)` positionally (their calls omit the new trailing optional parameter, so they should compile and pass unchanged; if either file passes `costPriceSnapshot` and then something else positionally where the new parameter would land, fix that call site to match — read both files first per this task's Files list before assuming no change is needed).

- [ ] **Step 8: Commit**

```bash
git add src/Negosio.Domain/Enums/DeliveryStatus.cs src/Negosio.Domain/Entities/Sales/SaleItem.cs src/Negosio.Domain/Entities/Sales/Sale.cs src/Negosio.Infrastructure/Persistence/Configurations/SaleItemConfiguration.cs tests/Negosio.UnitTests/Sales/SaleItemFulfillmentTests.cs
git commit -m "feat(sales): SaleItem carries a persisted DeliveryRequiredQuantity"
```

---

### Task 2: `DeliveryReceipt` domain entity — status lifecycle, scheduling, concurrency

**Files:**
- Modify: `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs` (full-file replacement — the constructor and factory both change shape)
- Modify: `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs` (extend — existing `Make()` helper's `Create(...)` call needs the new required parameters)
- Modify: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptSchemaTests.cs` (its own direct `Create(...)` call needs the same update)

**Interfaces:**
- Consumes: `DeliveryStatus` from Task 1.
- Produces: the new `DeliveryReceipt.Create(...)` signature (below), `MarkDelivered(Guid, DateTime)`, `Cancel(Guid, string, DateTime)`, `Status`, `SequenceNumber`, `ScheduledDeliveryDate`, `BatchRequestId`, `RowVersion`, and the four delivered/cancelled audit properties — Task 4 (EF config), Task 9-11 (service methods), and the migration (Task 5) all depend on this exact shape.

- [ ] **Step 1: Write the failing tests**

In `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs`, replace the `Make()` helper (it's called by every existing test in the file, so this one change updates all of them at once):

```csharp
    private static DeliveryReceipt Make() => DeliveryReceipt.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
        sequenceNumber: 1, scheduledDeliveryDate: new DateOnly(2026, 9, 15),
        "  Juan Dela Cruz ", " 123 Ayala Ave, Makati ", " 0917 111 2222 ", "  Leave at guardhouse ",
        Guid.NewGuid(), " Cashier One ");
```

Fix the two `blankName`/`blankAddr` calls in `Create_requires_recipient_and_address` the same way (insert `sequenceNumber: 1, scheduledDeliveryDate: new DateOnly(2026, 9, 15)` in the matching position — read the existing calls first, they currently pass `Guid.NewGuid(), Guid.NewGuid(), null, null, "...", "...", null, null, Guid.NewGuid(), "x"` positionally).

Then add new facts to the same class:

```csharp
    [Fact]
    public void Create_defaults_to_pending_status_with_no_audit_fields_set()
    {
        var dr = Make();

        dr.Status.Should().Be(DeliveryStatus.Pending);
        dr.SequenceNumber.Should().Be(1);
        dr.ScheduledDeliveryDate.Should().Be(new DateOnly(2026, 9, 15));
        dr.DeliveredAtUtc.Should().BeNull();
        dr.DeliveredByUserId.Should().BeNull();
        dr.CancelledAtUtc.Should().BeNull();
        dr.CancelledByUserId.Should().BeNull();
        dr.CancellationReason.Should().BeNull();
    }

    [Fact]
    public void Create_rejects_a_sequence_number_below_one()
    {
        var act = () => DeliveryReceipt.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000042",
            sequenceNumber: 0, scheduledDeliveryDate: new DateOnly(2026, 9, 15),
            "Juan", "123 Ayala Ave", null, null, Guid.NewGuid(), "x");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void MarkDelivered_transitions_pending_to_delivered_and_stamps_audit_fields()
    {
        var dr = Make();
        var deliveredBy = Guid.NewGuid();
        var deliveredAt = new DateTime(2026, 9, 16, 10, 0, 0, DateTimeKind.Utc);

        dr.MarkDelivered(deliveredBy, deliveredAt);

        dr.Status.Should().Be(DeliveryStatus.Delivered);
        dr.DeliveredByUserId.Should().Be(deliveredBy);
        dr.DeliveredAtUtc.Should().Be(deliveredAt);
    }

    [Fact]
    public void MarkDelivered_twice_throws()
    {
        var dr = Make();
        dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_transitions_pending_to_cancelled_and_stamps_audit_fields()
    {
        var dr = Make();
        var cancelledBy = Guid.NewGuid();
        var cancelledAt = new DateTime(2026, 9, 16, 11, 0, 0, DateTimeKind.Utc);

        dr.Cancel(cancelledBy, "  Customer rescheduled  ", cancelledAt);

        dr.Status.Should().Be(DeliveryStatus.Cancelled);
        dr.CancelledByUserId.Should().Be(cancelledBy);
        dr.CancelledAtUtc.Should().Be(cancelledAt);
        dr.CancellationReason.Should().Be("Customer rescheduled");
    }

    [Fact]
    public void Cancel_requires_a_non_blank_reason()
    {
        var dr = Make();

        var act = () => dr.Cancel(Guid.NewGuid(), "   ", DateTime.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cancel_after_delivered_throws()
    {
        var dr = Make();
        dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        var act = () => dr.Cancel(Guid.NewGuid(), "Too late", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void MarkDelivered_after_cancelled_throws()
    {
        var dr = Make();
        dr.Cancel(Guid.NewGuid(), "Changed mind", DateTime.UtcNow);

        var act = () => dr.MarkDelivered(Guid.NewGuid(), DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cancel_twice_throws()
    {
        var dr = Make();
        dr.Cancel(Guid.NewGuid(), "First reason", DateTime.UtcNow);

        var act = () => dr.Cancel(Guid.NewGuid(), "Second reason", DateTime.UtcNow);

        act.Should().Throw<InvalidOperationException>();
    }
```

(This file will need `using Negosio.Domain.Enums;` added to its usings for `DeliveryStatus` — check it isn't already there.)

In `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptSchemaTests.cs`, update its own `DeliveryReceipt.Create(...)` call (currently uses named arguments) to add `sequenceNumber: 1, scheduledDeliveryDate: new DateOnly(2026, 9, 15)` alongside the existing named arguments (read the current call first — it's fully named, so just insert the two new named arguments anywhere in the list).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.UnitTests --filter DeliveryReceiptEntityTests`
Expected: FAIL to compile — `Create` has no `sequenceNumber`/`scheduledDeliveryDate` parameters, `DeliveryStatus`/`MarkDelivered`/`Cancel` don't exist on `DeliveryReceipt`.

- [ ] **Step 3: Replace `DeliveryReceipt.cs` in full**

```csharp
using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// One scheduled (or completed, or cancelled) delivery against a <see cref="Sale"/>. A sale may have
/// several of these over time — <see cref="SequenceNumber"/> is a sale-scoped "Delivery 1", "Delivery
/// 2" label, never a global document number. Captures snapshot details of what's assigned to it
/// (recipient, address, items) at creation time. A cancelled record is kept forever as audit history —
/// rescheduling always creates a brand-new record with the next sequence number, never reactivates
/// this one. Identified solely by its <see cref="Entity.Id"/>.
/// </summary>
public class DeliveryReceipt : Entity
{
    private readonly List<DeliveryReceiptItem> _items = new();

    private DeliveryReceipt()
    {
        RecipientName = string.Empty;
        DeliveryAddress = string.Empty;
        PreparedByNameSnapshot = string.Empty;
    }

    private DeliveryReceipt(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDeliveryDate,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? deliveryNotes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        SaleId = saleId;
        RelatedSaleNumber = relatedSaleNumber;
        SequenceNumber = sequenceNumber;
        ScheduledDeliveryDate = scheduledDeliveryDate;
        RecipientName = recipientName;
        DeliveryAddress = deliveryAddress;
        ContactNumber = contactNumber;
        DeliveryNotes = deliveryNotes;
        PreparedByUserId = preparedByUserId;
        PreparedByNameSnapshot = preparedByNameSnapshot;
        BatchRequestId = batchRequestId;
        Status = DeliveryStatus.Pending;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid? SaleId { get; private set; }

    public string? RelatedSaleNumber { get; private set; }

    /// <summary>Sale-scoped label ("Delivery 1", "Delivery 2", ...) — never a global DR number.</summary>
    public int SequenceNumber { get; private set; }

    public DateOnly ScheduledDeliveryDate { get; private set; }

    public DeliveryStatus Status { get; private set; }

    public string RecipientName { get; private set; }

    public string DeliveryAddress { get; private set; }

    public string? ContactNumber { get; private set; }

    public string? DeliveryNotes { get; private set; }

    public Guid PreparedByUserId { get; private set; }

    public string PreparedByNameSnapshot { get; private set; }

    public DateTime? DeliveredAtUtc { get; private set; }

    public Guid? DeliveredByUserId { get; private set; }

    public DateTime? CancelledAtUtc { get; private set; }

    public Guid? CancelledByUserId { get; private set; }

    public string? CancellationReason { get; private set; }

    /// <summary>Client-supplied idempotency key for the batch-create call that produced this record
    /// (and every sibling created in the same batch) — null for a delivery created one at a time
    /// outside a batch. See the plan's Global Constraints for the idempotency strategy.</summary>
    public Guid? BatchRequestId { get; private set; }

    /// <summary>SQL Server `rowversion` — EF-managed optimistic concurrency token, never set by
    /// application code. Protects two competing status changes on the same delivery (mark-delivered
    /// racing cancel, or either racing itself from two tabs).</summary>
    public byte[] RowVersion { get; private set; } = Array.Empty<byte>();

    public IReadOnlyCollection<DeliveryReceiptItem> Items => _items.AsReadOnly();

    public static DeliveryReceipt Create(
        Guid tenantId,
        Guid branchId,
        Guid? saleId,
        string? relatedSaleNumber,
        int sequenceNumber,
        DateOnly scheduledDeliveryDate,
        string recipientName,
        string deliveryAddress,
        string? contactNumber,
        string? deliveryNotes,
        Guid preparedByUserId,
        string preparedByNameSnapshot,
        Guid? batchRequestId = null)
    {
        if (sequenceNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number must be at least 1.");
        }

        var trimmedRecipientName = recipientName?.Trim() ?? string.Empty;
        var trimmedDeliveryAddress = deliveryAddress?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(trimmedRecipientName))
        {
            throw new ArgumentException("Recipient name is required.", nameof(recipientName));
        }

        if (string.IsNullOrWhiteSpace(trimmedDeliveryAddress))
        {
            throw new ArgumentException("Delivery address is required.", nameof(deliveryAddress));
        }

        var trimmedContactNumber = contactNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedContactNumber))
        {
            trimmedContactNumber = null;
        }

        var trimmedDeliveryNotes = deliveryNotes?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDeliveryNotes))
        {
            trimmedDeliveryNotes = null;
        }

        var trimmedRelatedSaleNumber = relatedSaleNumber?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedRelatedSaleNumber))
        {
            trimmedRelatedSaleNumber = null;
        }

        var trimmedPreparedByNameSnapshot = preparedByNameSnapshot?.Trim() ?? string.Empty;

        return new DeliveryReceipt(
            tenantId,
            branchId,
            saleId,
            trimmedRelatedSaleNumber,
            sequenceNumber,
            scheduledDeliveryDate,
            trimmedRecipientName,
            trimmedDeliveryAddress,
            trimmedContactNumber,
            trimmedDeliveryNotes,
            preparedByUserId,
            trimmedPreparedByNameSnapshot,
            batchRequestId);
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
            TenantId,
            Id,
            saleItemId,
            productNameSnapshot,
            variantNameSnapshot,
            quantity,
            unitPrice);
        _items.Add(item);
        return item;
    }

    /// <summary>Completes every item/quantity assigned to this delivery. <paramref name="deliveredAtUtc"/>
    /// is required, not read from the clock here — the caller captures one TimeProvider-sourced
    /// timestamp per attempt, matching <see cref="Sale.Void"/>'s established pattern.</summary>
    public void MarkDelivered(Guid deliveredByUserId, DateTime deliveredAtUtc)
    {
        if (Status != DeliveryStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending delivery can be marked delivered.");
        }

        Status = DeliveryStatus.Delivered;
        DeliveredAtUtc = deliveredAtUtc;
        DeliveredByUserId = deliveredByUserId;
        Touch();
    }

    /// <summary>Releases this delivery's quantities back to Unscheduled without deleting the record —
    /// it stays forever as audit history. Does not touch the Sale or its DeliveryCharge. Rescheduling
    /// creates a brand-new Pending record elsewhere; this method never reactivates one.</summary>
    public void Cancel(Guid cancelledByUserId, string reason, DateTime cancelledAtUtc)
    {
        if (Status != DeliveryStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending delivery can be cancelled.");
        }

        var trimmedReason = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedReason))
        {
            throw new ArgumentException("A cancellation reason is required.", nameof(reason));
        }

        Status = DeliveryStatus.Cancelled;
        CancelledAtUtc = cancelledAtUtc;
        CancelledByUserId = cancelledByUserId;
        CancellationReason = trimmedReason;
        Touch();
    }
}
```

Note `AddItem` gained a new required first parameter, `saleItemId` — Task 3 updates `DeliveryReceiptItem` to match; Task 9/10's service code is the only caller and is written against this new shape from the start.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.UnitTests --filter DeliveryReceiptEntityTests`
Expected: FAIL again at this point — `AddItem`'s new `saleItemId` parameter and `DeliveryReceiptItem`'s matching constructor change (Task 3) aren't done yet, so anything in this test file that calls `AddItem` won't compile. That's expected; Task 3 (next) completes the pair. Do not consider this task done until Task 3's own test run is green — commit this task's work now regardless, per the step below, since the plan tracks them as separate reviewable commits.

- [ ] **Step 5: Commit**

```bash
git add src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptSchemaTests.cs
git commit -m "feat(delivery): DeliveryReceipt gains a Pending/Delivered/Cancelled lifecycle and scheduling fields"
```

---

### Task 3: `DeliveryReceiptItem` — link to its real `SaleItem`

**Files:**
- Modify: `src/Negosio.Domain/Entities/Delivery/DeliveryReceiptItem.cs` (full-file replacement)
- Modify: `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs` (its `AddItem_snapshots_line_and_rejects_zero_qty` test's two `dr.AddItem(...)` calls need the new leading `saleItemId` argument)

**Interfaces:**
- Consumes: `DeliveryReceipt.AddItem(saleItemId, ...)`'s new signature from Task 2.
- Produces: `DeliveryReceiptItem.SaleItemId` (`Guid`) — Task 4 (EF config + migration backfill), Task 9-11 (allocation queries join on this), and every fulfillment-quantity computation in this plan depend on it.

- [ ] **Step 1: Write the failing test**

In `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs`, update `AddItem_snapshots_line_and_rejects_zero_qty`:

```csharp
    [Fact]
    public void AddItem_snapshots_line_and_rejects_zero_qty()
    {
        var dr = Make();
        var saleItemId = Guid.NewGuid();

        dr.AddItem(saleItemId, "Coke 1.5L", null, 3m, 85m);
        dr.Items.Should().ContainSingle();
        dr.Items.Single().SaleItemId.Should().Be(saleItemId);
        dr.Items.Single().UnitPrice.Should().Be(85m);
        dr.Items.Single().ProductNameSnapshot.Should().Be("Coke 1.5L");
        dr.Items.Single().Quantity.Should().Be(3m);

        var act = () => dr.AddItem(Guid.NewGuid(), "Bad", null, 0m, null);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Negosio.UnitTests --filter DeliveryReceiptEntityTests`
Expected: FAIL to compile — `DeliveryReceiptItem` has no `SaleItemId` member yet.

- [ ] **Step 3: Replace `DeliveryReceiptItem.cs` in full**

```csharp
using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>One line item in a <see cref="DeliveryReceipt"/>: which <see cref="SaleItem"/> and how
/// much of it is assigned to this specific delivery, plus a snapshot of product details at delivery
/// time (never re-read from the live Product record). <see cref="SaleItemId"/> is what fulfillment
/// allocation (Pending/Delivered/Unscheduled quantity per sale item) is computed from.</summary>
public class DeliveryReceiptItem : Entity
{
    private DeliveryReceiptItem()
    {
        ProductNameSnapshot = string.Empty;
    }

    internal DeliveryReceiptItem(
        Guid tenantId,
        Guid deliveryReceiptId,
        Guid saleItemId,
        string productNameSnapshot,
        string? variantNameSnapshot,
        decimal quantity,
        decimal? unitPrice)
    {
        TenantId = tenantId;
        DeliveryReceiptId = deliveryReceiptId;
        SaleItemId = saleItemId;
        ProductNameSnapshot = productNameSnapshot;
        VariantNameSnapshot = variantNameSnapshot;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid TenantId { get; private set; }

    public Guid DeliveryReceiptId { get; private set; }

    public Guid SaleItemId { get; private set; }

    public string ProductNameSnapshot { get; private set; }

    public string? VariantNameSnapshot { get; private set; }

    public decimal Quantity { get; private set; }

    public decimal? UnitPrice { get; private set; }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.UnitTests --filter DeliveryReceiptEntityTests`
Expected: PASS — every test in the file, including all of Task 2's new ones.

- [ ] **Step 5: Commit**

```bash
git add src/Negosio.Domain/Entities/Delivery/DeliveryReceiptItem.cs tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs
git commit -m "feat(delivery): DeliveryReceiptItem links to its real SaleItem"
```

---

### Task 4: EF Core configuration for the new columns, indexes, and FK

**Files:**
- Modify: `src/Negosio.Infrastructure/Persistence/Configurations/DeliveryReceiptConfiguration.cs` (full-file replacement — both `DeliveryReceiptConfiguration` and `DeliveryReceiptItemConfiguration` live in this one file)

**Interfaces:**
- Consumes: every new property from Tasks 2-3.
- Produces: the exact EF model Task 5's migration diffs against. No test of its own — this task is schema-declaration only; Task 5 (migration) and every integration test from Task 9 onward is what actually exercises it against a real database.

- [ ] **Step 1: Replace the file in full**

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Infrastructure.Persistence.Configurations;

public sealed class DeliveryReceiptConfiguration : IEntityTypeConfiguration<DeliveryReceipt>
{
    public void Configure(EntityTypeBuilder<DeliveryReceipt> b)
    {
        b.ToTable("DeliveryReceipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();

        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.BranchId).IsRequired();
        b.Property(x => x.SaleId);
        b.Property(x => x.RelatedSaleNumber).HasMaxLength(30).IsUnicode(false);
        // The default values below are inert for the application (the domain constructor always sets
        // SequenceNumber/Status explicitly on every insert, so EF always sends the real value) — they
        // exist purely so the migration's one-time legacy-row backfill has a value to fall back to
        // without diverging the column's steady-state model from what this migration actually applies.
        b.Property(x => x.SequenceNumber).IsRequired().HasDefaultValue(1);
        b.Property(x => x.ScheduledDeliveryDate).IsRequired();
        b.Property(x => x.Status).IsRequired().HasConversion<int>().HasDefaultValue(DeliveryStatus.Delivered);
        b.Property(x => x.RecipientName).IsRequired().HasMaxLength(120);
        b.Property(x => x.DeliveryAddress).IsRequired().HasMaxLength(300);
        b.Property(x => x.ContactNumber).HasMaxLength(40);
        b.Property(x => x.DeliveryNotes).HasMaxLength(1000);
        b.Property(x => x.PreparedByUserId).IsRequired();
        b.Property(x => x.PreparedByNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.DeliveredAtUtc);
        b.Property(x => x.DeliveredByUserId);
        b.Property(x => x.CancelledAtUtc);
        b.Property(x => x.CancelledByUserId);
        b.Property(x => x.CancellationReason).HasMaxLength(500);
        b.Property(x => x.BatchRequestId);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<Sale>().WithMany().HasForeignKey(x => x.SaleId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.DeliveryReceiptId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Sale-scoped "Delivery 1"/"Delivery 2" label — unique per sale, never a global DR number.
        // SaleId is nullable, so SQL Server treats each NULL as distinct — a delivery with no linked
        // sale never collides with another for this uniqueness check.
        b.HasIndex(x => new { x.SaleId, x.SequenceNumber })
            .IsUnique().HasDatabaseName("IX_DeliveryReceipts_SaleId_SequenceNumber");

        // Retained for "list every delivery for this sale" queries — now genuinely multi-row per sale.
        b.HasIndex(x => new { x.TenantId, x.SaleId })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_SaleId");

        // Batch-create idempotency: a retry with the same BatchRequestId must find (not duplicate) the
        // batch's own rows. Filtered so ad-hoc (non-batch) deliveries, which leave this null, never
        // collide with each other under SQL Server's NULL-is-distinct default — the filter here makes
        // that explicit and matches the "no allocation" intent for those rows either way.
        b.HasIndex(x => new { x.TenantId, x.BatchRequestId })
            .IsUnique()
            .HasFilter("[BatchRequestId] IS NOT NULL")
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_BatchRequestId");

        // Report/dashboard access patterns: newest-first within tenant+branch, and by schedule date.
        b.HasIndex(x => new { x.TenantId, x.BranchId, x.CreatedAtUtc })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_BranchId_CreatedAtUtc");
        b.HasIndex(x => new { x.TenantId, x.Status, x.ScheduledDeliveryDate })
            .HasDatabaseName("IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate");
    }
}

public sealed class DeliveryReceiptItemConfiguration : IEntityTypeConfiguration<DeliveryReceiptItem>
{
    public void Configure(EntityTypeBuilder<DeliveryReceiptItem> b)
    {
        b.ToTable("DeliveryReceiptItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TenantId).IsRequired();
        b.Property(x => x.DeliveryReceiptId).IsRequired();
        b.Property(x => x.SaleItemId).IsRequired();
        b.Property(x => x.ProductNameSnapshot).IsRequired().HasMaxLength(200);
        b.Property(x => x.VariantNameSnapshot).HasMaxLength(200);
        b.Property(x => x.Quantity).HasPrecision(18, 3);
        b.Property(x => x.UnitPrice).HasPrecision(18, 2);
        b.Property(x => x.CreatedAtUtc).IsRequired();
        b.Property(x => x.UpdatedAtUtc).IsRequired();

        b.HasOne<SaleItem>().WithMany().HasForeignKey(x => x.SaleItemId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TenantId, x.DeliveryReceiptId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_DeliveryReceiptId");

        // A sale item cannot appear twice within the same delivery.
        b.HasIndex(x => new { x.DeliveryReceiptId, x.SaleItemId })
            .IsUnique().HasDatabaseName("IX_DeliveryReceiptItems_DeliveryReceiptId_SaleItemId");

        // Fulfillment allocation (Pending/Delivered/Unscheduled per sale item) is computed by joining
        // this column back to its sale item's assignments across every non-cancelled delivery.
        b.HasIndex(x => new { x.TenantId, x.SaleItemId })
            .HasDatabaseName("IX_DeliveryReceiptItems_TenantId_SaleItemId");
    }
}
```

- [ ] **Step 2: Confirm the project builds**

Run: `dotnet build src/Negosio.Infrastructure`
Expected: 0 errors. (The migration itself — Task 5 — is what actually proves this configuration is correct against a real database; this step only confirms it compiles.)

- [ ] **Step 3: Commit**

```bash
git add src/Negosio.Infrastructure/Persistence/Configurations/DeliveryReceiptConfiguration.cs
git commit -m "feat(delivery): EF configuration for the delivery-fulfillment schema"
```

---

### Task 5: EF Core migration — schema + legacy-data backfill

**This is the highest-risk task in the plan — read it fully before starting, and do not skip the verification steps.**

**Files:**
- Create: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/<timestamp>_AddDeliveryFulfillment.cs` (+ `.Designer.cs`) — auto-generated, then **hand-edited**
- Modify: `src/Negosio.Infrastructure/Persistence/Migrations/Tenant/TenantDbContextModelSnapshot.cs` (auto-updated — do not hand-edit)

**Interfaces:**
- Consumes: Tasks 1-4's full domain + EF-config shape.
- Produces: the real columns/indexes every later backend task depends on, applied to the local tenant database. Every integration test from Task 9 onward needs this applied first.

No automated test of its own — verified by careful manual inspection plus applying it to a real LocalDB and re-running the full existing suite (which must still pass unchanged — this is the proof the backfill didn't corrupt any existing sale/delivery data).

- [ ] **Step 1: Ensure the EF tool is restored, generate the migration**

```bash
dotnet tool restore
dotnet dotnet-ef migrations add AddDeliveryFulfillment --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb
```

If this fails to build first, stop and check `dotnet build Negosio.sln` — Tasks 1-4 should already leave the solution compiling cleanly; do not proceed on a broken build.

- [ ] **Step 2: Inspect what EF generated, then hand-edit `Up()`**

EF will scaffold, roughly: `AddColumn` for every new `SaleItems`/`DeliveryReceipts`/`DeliveryReceiptItems` column, `CreateIndex` for every new index, and (because `SaleItemId` and `ScheduledDeliveryDate` are `IsRequired()` with **no** default in the model) it will very likely generate them as `nullable: false` with **no** `defaultValue` — which fails outright against a database with existing rows. You must restructure `Up()` into this exact order (renumbering EF's own generated statements into these phases, not inventing new ones — the column definitions/types below should match what EF actually scaffolded; adjust only nullability/defaultValue/ordering):

**Phase 1 — add everything EF scaffolded, but with the two backfill-needed columns temporarily nullable:**
- `SaleItems.DeliveryRequiredQuantity` — as EF scaffolded it (`nullable: false, defaultValue: 0m` — this one's fine as-is, every existing `SaleItem` correctly defaults to fully Take-now).
- `DeliveryReceipts.SequenceNumber` — as EF scaffolded (`nullable: false, defaultValue: 1`).
- `DeliveryReceipts.ScheduledDeliveryDate` — **change to `nullable: true`** regardless of what EF scaffolded (no sensible static default exists; every row gets backfilled per-row in Phase 2 below).
- `DeliveryReceipts.Status` — as EF scaffolded (`nullable: false, defaultValue: 2` — confirm `2` is what got emitted for `DeliveryStatus.Delivered`, since enum values are 1-based in this codebase; if EF emitted `1` instead, your `HasConversion<int>()`/`HasDefaultValue` in Task 4 didn't take effect as expected — stop and fix Task 4's config rather than hand-patching the number here).
- `DeliveryReceipts.DeliveredAtUtc`, `DeliveredByUserId`, `CancelledAtUtc`, `CancelledByUserId`, `CancellationReason`, `BatchRequestId`, `RowVersion` — all as EF scaffolded (all nullable already, or `rowversion` which SQL Server auto-populates on existing rows with no backfill needed).
- `DeliveryReceiptItems.SaleItemId` — **change to `nullable: true`** regardless of what EF scaffolded (backfilled per-row in Phase 2).
- Every `CreateIndex` call **except** the two on `ScheduledDeliveryDate`/`SaleItemId`-involving columns that would fail against still-null data — move `IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate` and `IX_DeliveryReceiptItems_DeliveryReceiptId_SaleItemId`/`IX_DeliveryReceiptItems_TenantId_SaleItemId` to **Phase 3**, after the backfill.

**Phase 2 — insert this hand-written backfill, as `migrationBuilder.Sql(...)` calls, right after Phase 1's `AddColumn`s and before any `AlterColumn`/remaining `CreateIndex`:**

```csharp
            // Legacy DeliveryReceipts were always whole-sale, one row per SaleItem, created by
            // ResolveItems(sale, request) with `request.Items is null` OR an explicit but exhaustive
            // list — either way, each DeliveryReceiptItem's (ProductNameSnapshot, VariantNameSnapshot)
            // pair matches exactly one SaleItem on that DeliveryReceipt's Sale, because checkout already
            // merges duplicate-variant lines into one SaleItem per variant per sale. Both sides are
            // immutable point-in-time snapshots from the same sale event, so this match is exact.
            migrationBuilder.Sql(@"
                UPDATE dri
                SET dri.SaleItemId = si.Id
                FROM DeliveryReceiptItems dri
                JOIN DeliveryReceipts dr ON dr.Id = dri.DeliveryReceiptId
                JOIN SaleItems si ON si.SaleId = dr.SaleId
                    AND si.ProductNameSnapshot = dri.ProductNameSnapshot
                    AND ISNULL(si.VariantNameSnapshot, N'') = ISNULL(dri.VariantNameSnapshot, N'')
                WHERE dri.SaleItemId IS NULL;
            ");

            // Best available historical value: the DR's own creation date. Documented assumption, not
            // a precise "when the truck actually arrived" timestamp — none was ever captured pre-plan.
            migrationBuilder.Sql(@"
                UPDATE DeliveryReceipts
                SET ScheduledDeliveryDate = CAST(CreatedAtUtc AS date)
                WHERE ScheduledDeliveryDate IS NULL;
            ");

            // Best available historical actor/time for "delivered" — DeliveredAtUtc/DeliveredByUserId
            // stay nullable (only meaningful once Delivered), but every legacy row defaulted Status to
            // Delivered in Phase 1, so populate them consistently rather than leaving a Delivered row
            // with null delivered-audit fields. Documented assumption: PreparedByUserId (the only actor
            // ever recorded before this plan) stands in for "delivered by," and CreatedAtUtc stands in
            // for "delivered at" — no better data exists.
            migrationBuilder.Sql(@"
                UPDATE DeliveryReceipts
                SET DeliveredAtUtc = CreatedAtUtc, DeliveredByUserId = PreparedByUserId
                WHERE Status = 2 AND DeliveredAtUtc IS NULL;
            ");

            // Every SaleItem on a sale that never had a delivery receipt correctly keeps
            // DeliveryRequiredQuantity = 0 from Phase 1's defaultValue — no further backfill needed;
            // those items stay entirely Take-now, which is the existing (pre-plan) behavior unchanged.
            // For sale items that WERE on a legacy (now-Delivered) delivery receipt, their full sold
            // quantity was delivery-required (the old feature had no concept of partial/take-now split):
            migrationBuilder.Sql(@"
                UPDATE si
                SET si.DeliveryRequiredQuantity = si.Quantity
                FROM SaleItems si
                WHERE EXISTS (
                    SELECT 1 FROM DeliveryReceiptItems dri WHERE dri.SaleItemId = si.Id
                );
            ");
```

**Phase 3 — after the backfill, alter the two temporarily-nullable columns to `NOT NULL`, then add the remaining indexes:**

```csharp
            migrationBuilder.AlterColumn<DateOnly>(
                name: "ScheduledDeliveryDate", table: "DeliveryReceipts", type: "date", nullable: false,
                oldClrType: typeof(DateOnly), oldType: "date", oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "SaleItemId", table: "DeliveryReceiptItems", nullable: false,
                oldClrType: typeof(Guid), oldNullable: true);
```
(Adjust the exact `AlterColumn` syntax/types to match what EF's own style produces elsewhere in this migrations folder if it differs — the intent is "these two columns end up NOT NULL, with everything already backfilled by Phase 2.")

Then the two indexes deferred from Phase 1 (`IX_DeliveryReceipts_TenantId_Status_ScheduledDeliveryDate`, `IX_DeliveryReceiptItems_DeliveryReceiptId_SaleItemId`, `IX_DeliveryReceiptItems_TenantId_SaleItemId`) — this ordering matters: `AlterColumn` to `NOT NULL` would itself fail if Phase 2 left any row unmatched, which is exactly the self-verifying safety net the plan's Global Constraints call for — **if this step fails, stop, do not force it, and investigate which row didn't match** (most likely cause: a pre-plan DR whose sale/items were later edited in a way not anticipated here — there shouldn't be any, since `SaleItem`s are never deleted or renamed after checkout, but verify for real against this machine's actual seeded/test data rather than assuming).

- [ ] **Step 3: Write the mirrored `Down()`**

`Down()` should reverse Phase 3's `AlterColumn`s back to nullable, drop the Phase-3 indexes, then drop every column/index Phase 1 added — EF's own scaffolded `Down()` is a reasonable starting point; adjust ordering to be the exact reverse of the hand-edited `Up()`.

- [ ] **Step 4: Apply the migration and verify existing data**

```bash
dotnet dotnet-ef database update --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api
```
Expected: succeeds with no errors.

Then, against the same LocalDB tenant database this repo already uses for integration tests, spot-check with a raw query (adjust the connection/tool to however you can reach it — SSMS, `sqlcmd`, or a throwaway C# script is all fine, this is a one-time manual check, not a permanent test):
- Every existing `DeliveryReceipts` row has `Status = 2` (Delivered), a non-null `ScheduledDeliveryDate`, `SequenceNumber = 1`.
- Every existing `DeliveryReceiptItems` row has a non-null `SaleItemId` that actually exists in `SaleItems`.
- Every `SaleItems` row that has a matching `DeliveryReceiptItems` row now has `DeliveryRequiredQuantity = Quantity`; every other `SaleItems` row still has `DeliveryRequiredQuantity = 0`.

- [ ] **Step 5: Run the full pre-existing backend suite to confirm nothing broke**

Run: `dotnet test`
Expected: every pre-existing test still passes (this proves the backfill didn't corrupt any sale/delivery-receipt data the existing `DeliveryReceiptTests.cs`/`SaleQueryTests.cs`/etc. depend on). Some tests in `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs` will very likely now FAIL to *compile* (not just run) once Task 9-11 change `IDeliveryReceiptService`'s interface shape — if that happens at this point, it means you're seeing Task 9+'s breakage early; that's fine, note it and move on, Task 9-11 own fixing those call sites. If instead everything compiles and something *fails at runtime*, stop and investigate before continuing — that would mean the migration itself broke something.

- [ ] **Step 6: Commit**

```bash
git add src/Negosio.Infrastructure/Persistence/Migrations/Tenant/
git commit -m "feat(db): migration for delivery fulfillment (status, scheduling, SaleItem linkage) with legacy-data backfill"
```

---

### Task 6: New error codes + `DeliveryCancel` authorization policy

**Files:**
- Modify: `src/Negosio.Application/Common/ErrorCodes.cs`
- Modify: `src/Negosio.Api/Authorization/AuthorizationPolicies.cs`

**Interfaces:**
- Produces: every `ErrorCodes.*` constant and `AuthorizationPolicies.DeliveryCancel` that later tasks reference by name.

No test of its own — these are constants; the tasks that use them carry the coverage. Verified by a clean build only.

- [ ] **Step 1: Add the new error codes**

In `ErrorCodes.cs`, after the existing `// ---- Delivery receipts ----` block:

```csharp
    // ---- Delivery receipts ----
    public const string DeliveryReceiptNotFound = "DELIVERY_RECEIPT_NOT_FOUND";
    public const string DeliveryReceiptNotAllowed = "DELIVERY_RECEIPT_NOT_ALLOWED";
    public const string DeliveryScheduleDateInPast = "DELIVERY_SCHEDULE_DATE_IN_PAST";
    public const string DeliveryQuantityExceedsAvailable = "DELIVERY_QUANTITY_EXCEEDS_AVAILABLE";
    public const string DeliveryDuplicateSaleItem = "DELIVERY_DUPLICATE_SALE_ITEM";
    public const string DeliveryNoUnscheduledQuantity = "DELIVERY_NO_UNSCHEDULED_QUANTITY";
    public const string DeliveryReceiptNotPending = "DELIVERY_RECEIPT_NOT_PENDING";
    public const string DeliveryCancellationReasonRequired = "DELIVERY_CANCELLATION_REASON_REQUIRED";
    public const string DeliveryReceiptConcurrencyConflict = "DELIVERY_RECEIPT_CONCURRENCY_CONFLICT";
```

- [ ] **Step 2: Add the `DeliveryCancel` policy**

In `AuthorizationPolicies.cs`, add the constant next to `ReceiptSettingsManage`:

```csharp
    /// <summary>Cancel a Pending delivery — Owner/Admin/Manager only (spec: "Owner, Admin, Manager, or
    /// equivalent"). View/Create/MarkDelivered stay on the broader SalesView (PosRoles) policy — only
    /// cancellation gets its own gate.</summary>
    public const string DeliveryCancel = "DeliveryCancel";
```

And register it in `AddNegosioPolicies`, next to `ReceiptSettingsManage`'s registration:

```csharp
        options.AddPolicy(DeliveryCancel, policy =>
            policy.RequireAuthenticatedUser()
                  .RequireClaim(JwtTokenGenerator.RoleClaimType, RoleNames(ManagementRoles)));
```

- [ ] **Step 3: Build**

Run: `dotnet build Negosio.sln`
Expected: builds clean (nothing yet references the new members beyond this file).

- [ ] **Step 4: Commit**

```bash
git add src/Negosio.Application/Common/ErrorCodes.cs src/Negosio.Api/Authorization/AuthorizationPolicies.cs
git commit -m "feat(delivery): error codes and DeliveryCancel policy for the fulfillment workflow"
```

---

### Task 7: Checkout — accept and persist `DeliveryRequiredQuantity`, return created SaleItem identifiers

**Files:**
- Modify: `src/Negosio.Application/Pos/CheckoutContracts.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutValidator.cs`
- Modify: `src/Negosio.Application/Pos/CheckoutService.cs`
- Test: `tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryFulfillmentTests.cs` (new)

**Interfaces:**
- Consumes: `Sale.AddItem(..., decimal deliveryRequiredQuantity = 0m)` from Task 1.
- Produces: `CheckoutItemInput.DeliveryRequiredQuantity`, `SaleResultDto.Items` (`IReadOnlyList<SaleResultItemDto>`) — the created `SaleItem` identifiers the POS frontend needs to submit the follow-up delivery-schedule batch (Task 9's `POST /api/sales/{saleId}/delivery-receipts/batch`). Every later checkout caller (frontend Task 17-18) depends on this exact shape.

- [ ] **Step 1: Write the failing test**

Create `tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryFulfillmentTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Pos;

public class CheckoutDeliveryFulfillmentTests : IntegrationTest
{
    public CheckoutDeliveryFulfillmentTests(NegosioApiFactory factory) : base(factory)
    {
    }

    private sealed record Scene(Guid BranchId, Guid SessionId, Guid VariantId);

    private async Task<Scene> ArrangeAsync(decimal price = 100m, decimal stock = 50m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: stock);
        return new Scene(branchId, session.Id, variantId);
    }

    private static CheckoutRequest Sale(Scene s, decimal quantity, decimal deliveryRequiredQuantity, decimal cashReceived) => new(
        s.BranchId, s.SessionId, Guid.NewGuid(),
        new[] { new CheckoutItemInput(s.VariantId, quantity, null, deliveryRequiredQuantity) },
        new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: cashReceived) });

    [Fact]
    public async Task A_line_with_no_delivery_requirement_defaults_the_whole_quantity_to_take_now()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 0m, cashReceived: 500m));

        result.Items.Should().ContainSingle();
        result.Items[0].Quantity.Should().Be(5m);
        result.Items[0].DeliveryRequiredQuantity.Should().Be(0m);

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync();
            item.DeliveryRequiredQuantity.Should().Be(0m);
            item.TakeNowQuantity.Should().Be(5m);
            return true;
        });
    }

    [Fact]
    public async Task A_partial_delivery_requirement_splits_take_now_and_delivery_required_and_is_returned_for_scheduling()
    {
        var scene = await ArrangeAsync();

        var result = await CheckoutOkAsync(Sale(scene, quantity: 10m, deliveryRequiredQuantity: 6m, cashReceived: 1000m));

        result.Items[0].Quantity.Should().Be(10m);
        result.Items[0].DeliveryRequiredQuantity.Should().Be(6m);
        result.Items[0].SaleItemId.Should().NotBeEmpty();
        result.Items[0].ProductVariantId.Should().Be(scene.VariantId);

        await InScopeAsync(async db =>
        {
            var item = await db.SaleItems.SingleAsync();
            item.Id.Should().Be(result.Items[0].SaleItemId);
            item.DeliveryRequiredQuantity.Should().Be(6m);
            item.TakeNowQuantity.Should().Be(4m);
            return true;
        });
    }

    [Fact]
    public async Task Delivery_required_quantity_exceeding_the_sold_quantity_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: 5.5m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Negative_delivery_required_quantity_is_rejected()
    {
        var scene = await ArrangeAsync();

        var response = await CheckoutAsync(Sale(scene, quantity: 5m, deliveryRequiredQuantity: -1m, cashReceived: 500m));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Replaying_the_same_client_request_returns_the_same_created_SaleItem_identifiers()
    {
        var scene = await ArrangeAsync();
        var request = Sale(scene, quantity: 3m, deliveryRequiredQuantity: 2m, cashReceived: 300m);

        var first = await CheckoutOkAsync(request);
        var second = await CheckoutOkAsync(request);

        second.Items[0].SaleItemId.Should().Be(first.Items[0].SaleItemId);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter CheckoutDeliveryFulfillmentTests`
Expected: FAIL to compile — `CheckoutItemInput` has no 4th positional parameter, `SaleResultDto` has no `Items`.

- [ ] **Step 3: Extend the contracts**

In `CheckoutContracts.cs`, change `CheckoutItemInput` and `SaleResultDto`:

```csharp
public sealed record CheckoutItemInput(
    Guid ProductVariantId,
    decimal Quantity,
    CheckoutDiscountInput? Discount,
    /// <summary>How much of this line is not taken at the counter today. Defaults to 0 — the whole
    /// line is Take-now unless the cashier explicitly marks part of it for delivery. Never negative,
    /// never more than <see cref="Quantity"/> (validated below and, as a backstop, by
    /// <c>Sale.AddItem</c>).</summary>
    decimal DeliveryRequiredQuantity = 0m);
```

```csharp
public sealed record SaleResultItemDto(
    Guid SaleItemId,
    Guid ProductVariantId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal DeliveryRequiredQuantity);

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
    bool WasExistingRequest,
    /// <summary>The SaleItems this checkout created (or, on an idempotent replay, the SaleItems the
    /// original request created) — the frontend needs these ids to submit the follow-up delivery
    /// schedule batch for any line that carries a DeliveryRequiredQuantity > 0.</summary>
    IReadOnlyList<SaleResultItemDto> Items);
```

- [ ] **Step 4: Extend the validator**

In `CheckoutRequestValidator`, inside the existing `RuleForEach(x => x.Items).ChildRules(item => { ... })` block, add:

```csharp
            item.RuleFor(i => i.DeliveryRequiredQuantity)
                .GreaterThanOrEqualTo(0m).WithMessage("Delivery-required quantity cannot be negative.")
                .Must((i, deliveryQty) => deliveryQty <= i.Quantity)
                .WithMessage("Delivery-required quantity cannot exceed the item quantity.");
```

- [ ] **Step 5: Wire it through `CheckoutService`**

In `CheckoutContracts.cs` region of `CheckoutService.cs`, extend `ResolvedLine`:

```csharp
    private sealed record ResolvedLine(
        Guid ProductVariantId, decimal Quantity, decimal DeliveryRequiredQuantity, DiscountType DiscountType, decimal DiscountValue,
        string ProductNameSnapshot, string? VariantNameSnapshot, string? SkuSnapshot, string? BarcodeSnapshot,
        decimal UnitPrice, decimal CostPrice, bool TrackInventory, SaleLineCalculator.Line Amounts);
```

Update the merge step (§3) to sum `DeliveryRequiredQuantity` across duplicate-variant lines the same way `Quantity` is already summed — the per-line invariant `0 ≤ DeliveryRequiredQuantity ≤ Quantity` is preserved by summation, so no extra post-merge check is needed:

```csharp
        var merged = request.Items
            .GroupBy(i => i.ProductVariantId)
            .Select(g =>
            {
                var discount = g.Select(x => x.Discount).FirstOrDefault(d => d is { Type: not DiscountType.None })
                               ?? new CheckoutDiscountInput();
                return new CheckoutItemInput(g.Key, g.Sum(x => x.Quantity), discount, g.Sum(x => x.DeliveryRequiredQuantity));
            })
            .ToList();
```

In the per-line resolution loop (§4), pass the field through to `ResolvedLine`:

```csharp
            lines.Add(new ResolvedLine(
                variant.Id, item.Quantity, item.DeliveryRequiredQuantity, discount.Type, discount.Value,
                product.Name, variant.IsDefault ? null : variant.Name, variant.Sku, variant.Barcode,
                variant.SellingPrice, variant.CostPrice, product.TrackInventory, amounts));
```

In §6-10 (the transaction), capture the created `SaleItem`s instead of discarding `AddItem`'s return value:

```csharp
        var sale = Sale.Begin(tenantId, branch.Id, session.Id, saleNumber, request.ClientRequestId, _currentUser.UserId);
        var createdItems = new List<SaleItem>(lines.Count);
        foreach (var line in lines)
        {
            var saleItem = sale.AddItem(
                line.ProductVariantId, line.ProductNameSnapshot, line.VariantNameSnapshot, line.SkuSnapshot, line.BarcodeSnapshot,
                line.UnitPrice, line.Quantity, line.DiscountType, line.DiscountValue,
                line.Amounts.Gross, line.Amounts.Discount, line.Amounts.Tax, line.Amounts.Net, line.CostPrice,
                line.DeliveryRequiredQuantity);
            createdItems.Add(saleItem);
        }
```

(`Negosio.Domain.Entities.SaleItem` is already in scope via the existing `using Negosio.Domain.Entities;`.)

Both places that load an existing/winning sale for the idempotent-replay paths must now include `Items`, since `ToResult` needs them:

```csharp
        var existing = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.ClientRequestId == request.ClientRequestId, cancellationToken);
```

```csharp
                var winner = await _db.Sales.AsNoTracking()
                    .Include(s => s.Items)
                    .FirstAsync(s => s.TenantId == tenantId && s.ClientRequestId == request.ClientRequestId, cancellationToken);
```

Finally, rewrite `ToResult`:

```csharp
    private static SaleResultDto ToResult(Sale sale, bool wasExisting) => new(
        sale.Id, sale.SaleNumber, sale.Status, sale.Subtotal, sale.DiscountTotal, sale.TaxTotal,
        sale.DeliveryCharge, sale.GrandTotal, sale.AmountPaid, sale.ChangeDue, wasExisting,
        sale.Items
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i => new SaleResultItemDto(
                i.Id, i.ProductVariantId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, i.DeliveryRequiredQuantity))
            .ToList());
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "CheckoutDeliveryFulfillmentTests|CheckoutDeliveryChargeTests|CheckoutTests|CheckoutIdempotencyTests|CheckoutConcurrencyTests"`
Expected: PASS — the new tests plus every pre-existing checkout test (all construct `CheckoutItemInput` positionally without the 4th argument, which defaults to `0m`, so they compile and behave unchanged).

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Application/Pos/CheckoutContracts.cs src/Negosio.Application/Pos/CheckoutValidator.cs src/Negosio.Application/Pos/CheckoutService.cs tests/Negosio.IntegrationTests/Pos/CheckoutDeliveryFulfillmentTests.cs
git commit -m "feat(pos): checkout accepts DeliveryRequiredQuantity per line and returns created SaleItem ids"
```

---

### Task 8: `DeliveryReceiptContracts.cs` — full rewrite for the fulfillment API surface

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs` (full-file replacement)
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptValidators.cs` (full-file replacement)

**Interfaces:**
- Consumes: `DeliveryStatus` (Task 1), `DeliveryReceipt`/`DeliveryReceiptItem` shape (Tasks 2-3).
- Produces: every DTO and the full `IDeliveryReceiptService` interface that Tasks 9-11 implement and Tasks 17-21 (frontend) consume. This is the single coordination point for the whole remaining backend+frontend surface — read it fully before starting any later task.

This task is types-only; it has no test of its own (a build-only step) and is **expected to leave `DeliveryReceiptService.cs`, `SalesController.cs`, and `DeliveryReceiptsController.cs` failing to compile** until Tasks 9-11 catch up — exactly the same "next task completes the pair" situation already established by Task 2 → Task 3. Do not attempt to fix those other files in this task.

- [ ] **Step 1: Replace `DeliveryReceiptContracts.cs` in full**

```csharp
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

// ---- Create (single schedule) ----

public sealed record CreateDeliveryReceiptItemInput(Guid SaleItemId, decimal Quantity);

/// <summary>
/// One delivery schedule to create — used both standalone (<see cref="IDeliveryReceiptService.CreateAsync"/>,
/// e.g. the Sale-detail page's "Create delivery" action, or "Schedule again" after a cancellation) and as
/// one entry of <see cref="CreateDeliveryReceiptBatchRequest.Schedules"/> (checkout's initial batch).
/// </summary>
public sealed record CreateDeliveryReceiptRequest(
    DateOnly ScheduledDeliveryDate,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    IReadOnlyList<CreateDeliveryReceiptItemInput> Items);

// ---- Create (batch, at checkout) ----

/// <summary>
/// Creates every initial delivery schedule for a just-completed sale in one transaction.
/// <see cref="BatchRequestId"/> is a client-generated idempotency key: retrying the same id after a
/// network failure returns the original batch's rows rather than creating duplicates (see the plan's
/// Global Constraints — same idiom as <c>CheckoutRequest.ClientRequestId</c>).
/// </summary>
public sealed record CreateDeliveryReceiptBatchRequest(
    Guid BatchRequestId,
    IReadOnlyList<CreateDeliveryReceiptRequest> Schedules);

public sealed record DeliveryReceiptBatchResultDto(
    IReadOnlyList<DeliveryReceiptDto> Created,
    /// <summary>True when this exact <see cref="CreateDeliveryReceiptBatchRequest.BatchRequestId"/> was
    /// already applied — <see cref="Created"/> is the original batch's rows, not new ones.</summary>
    bool WasExistingBatch);

// ---- Status changes ----

public sealed record CancelDeliveryReceiptRequest(string Reason);

// ---- Read ----

public sealed record DeliveryReceiptItemDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? Amount);

public sealed record DeliveryReceiptDto(
    Guid Id,
    Guid? SaleId,
    string? RelatedSaleNumber,
    /// <summary>Sale-scoped "Delivery {N}" label — never a global document number.</summary>
    int SequenceNumber,
    DateOnly ScheduledDeliveryDate,
    DeliveryStatus Status,
    DateTime CreatedAtUtc,
    string BranchName,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    string PreparedByName,
    DateTime? DeliveredAtUtc,
    string? DeliveredByName,
    DateTime? CancelledAtUtc,
    string? CancelledByName,
    string? CancellationReason,
    IReadOnlyList<DeliveryReceiptItemDto> Items,
    /// <summary>Read live from the linked Sale (never a stored copy on this entity) — see the plan's
    /// Global Constraints. 0 for a delivery receipt with no linked sale.</summary>
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

// ---- Sale-level fulfillment view ----

public enum SaleFulfillmentStatus
{
    /// <summary>DeliveryRequiredQuantity is 0 across the whole sale — nothing was ever marked for
    /// delivery, so fulfillment tracking does not apply.</summary>
    NotApplicable = 1,
    Unscheduled = 2,
    PartiallyScheduled = 3,
    FullyScheduled = 4,
    PartiallyDelivered = 5,
    FullyDelivered = 6,
    /// <summary>At least one Pending schedule's ScheduledDeliveryDate is before business-local today.</summary>
    NeedsRescheduling = 7,
}

public sealed record SaleItemFulfillmentDto(
    Guid SaleItemId,
    string ProductName,
    string? VariantName,
    decimal Quantity,
    decimal TakeNowQuantity,
    decimal DeliveryRequiredQuantity,
    decimal PendingQuantity,
    decimal DeliveredQuantity,
    /// <summary>DeliveryRequiredQuantity − PendingQuantity − DeliveredQuantity. Never trust a
    /// frontend-computed version of this figure for a write — this is the read-side mirror of the
    /// same server-side quantity Task 9's allocation guard enforces.</summary>
    decimal AvailableToScheduleQuantity);

public sealed record DeliveryReceiptSummaryDto(
    Guid Id,
    int SequenceNumber,
    DateOnly ScheduledDeliveryDate,
    DeliveryStatus Status,
    string RecipientName,
    string DeliveryAddress,
    string? ContactNumber,
    string? DeliveryNotes,
    DateTime? DeliveredAtUtc,
    DateTime? CancelledAtUtc,
    string? CancellationReason,
    IReadOnlyList<DeliveryReceiptItemDto> Items);

public sealed record SaleDeliverySummaryDto(
    Guid SaleId,
    SaleFulfillmentStatus FulfillmentStatus,
    decimal DeliveryCharge,
    IReadOnlyList<SaleItemFulfillmentDto> Items,
    IReadOnlyList<DeliveryReceiptSummaryDto> Deliveries,
    /// <summary>True when at least one SaleItem has AvailableToScheduleQuantity > 0 — gates the
    /// Sale-detail page's "Create delivery" action. When false the UI shows: "All delivery items have
    /// already been scheduled or delivered."</summary>
    bool CanCreateDelivery);

public interface IDeliveryReceiptService
{
    Task<DeliveryReceiptBatchResultDto> CreateBatchAsync(
        Guid saleId, CreateDeliveryReceiptBatchRequest request, CancellationToken ct = default);

    Task<DeliveryReceiptDto> CreateAsync(
        Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<DeliveryReceiptDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default);

    Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default);

    Task<DeliveryReceiptDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default);

    Task<DeliveryReceiptDto> CancelAsync(Guid id, CancelDeliveryReceiptRequest request, CancellationToken ct = default);

    Task<SaleDeliverySummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Replace `DeliveryReceiptValidators.cs` in full**

```csharp
using FluentValidation;

namespace Negosio.Application.Delivery;

public sealed class CreateDeliveryReceiptItemInputValidator : AbstractValidator<CreateDeliveryReceiptItemInput>
{
    public CreateDeliveryReceiptItemInputValidator()
    {
        RuleFor(x => x.SaleItemId).NotEmpty();
        RuleFor(x => x.Quantity).GreaterThan(0m).WithMessage("Delivery quantity must be greater than zero.");
    }
}

public sealed class CreateDeliveryReceiptRequestValidator : AbstractValidator<CreateDeliveryReceiptRequest>
{
    public CreateDeliveryReceiptRequestValidator()
    {
        RuleFor(x => x.RecipientName).NotEmpty().WithMessage("Recipient name is required.").MaximumLength(120);
        RuleFor(x => x.DeliveryAddress).NotEmpty().WithMessage("Recipient address is required.").MaximumLength(300);
        RuleFor(x => x.ContactNumber).MaximumLength(40).When(x => x.ContactNumber != null);
        RuleFor(x => x.DeliveryNotes).MaximumLength(1000).When(x => x.DeliveryNotes != null);

        RuleFor(x => x.Items).NotEmpty().WithMessage("A delivery must include at least one item.");
        RuleForEach(x => x.Items).SetValidator(new CreateDeliveryReceiptItemInputValidator());
        RuleFor(x => x.Items)
            .Must(items => items.Select(i => i.SaleItemId).Distinct().Count() == items.Count)
            .WithMessage("Each sale item may be listed at most once per delivery.")
            .When(x => x.Items.Count > 0);

        // Business-local "today" — see the plan's Global Constraints (ReportPeriodResolver.BusinessOffset).
        // The validator has no access to TimeProvider (FluentValidation validators are singletons resolved
        // once by DI, not per-request), so this only catches an obviously-past date typed against the
        // client's own clock; the service re-checks against the server's business-local date, which is
        // the authoritative check (never trust the frontend/validator's clock alone for this).
        RuleFor(x => x.ScheduledDeliveryDate).NotEqual(default(DateOnly));
    }
}

public sealed class CreateDeliveryReceiptBatchRequestValidator : AbstractValidator<CreateDeliveryReceiptBatchRequest>
{
    public CreateDeliveryReceiptBatchRequestValidator()
    {
        RuleFor(x => x.BatchRequestId).NotEmpty();
        RuleFor(x => x.Schedules).NotEmpty().WithMessage("A batch must include at least one delivery schedule.");
        RuleForEach(x => x.Schedules).SetValidator(new CreateDeliveryReceiptRequestValidator());
    }
}

public sealed class CancelDeliveryReceiptRequestValidator : AbstractValidator<CancelDeliveryReceiptRequest>
{
    public CancelDeliveryReceiptRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("A cancellation reason is required.").MaximumLength(500);
    }
}
```

- [ ] **Step 3: Confirm the expected compile failures**

Run: `dotnet build Negosio.sln`
Expected: FAILS — `DeliveryReceiptService.cs` no longer implements `IDeliveryReceiptService` (old members removed), `SalesController.cs`/`DeliveryReceiptsController.cs` call methods that no longer exist. This is expected; Tasks 9-11 fix it. Confirm the failures are exactly these three files (no other unrelated breakage) before moving on.

- [ ] **Step 4: Commit**

```bash
git add src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs src/Negosio.Application/Delivery/DeliveryReceiptValidators.cs
git commit -m "feat(delivery): rewrite DeliveryReceiptContracts for scheduled, partial, multi-delivery fulfillment"
```

---

### Task 9: `DeliveryReceiptService` — read, create, and batch-create (allocation-guarded, idempotent)

**Files:**
- Create: `src/Negosio.Application/Delivery/SaleFulfillmentCalculator.cs`
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` (full-file replacement)
- Modify: `src/Negosio.Api/Controllers/SalesController.cs`
- Delete: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs` (replaced by the new file below — its scenarios are superseded by the new create/batch/list/fulfillment shape; the ones still relevant — rename-stability, branch-scoping, receipt-settings-driven price hiding, delivery-charge reflection — are carried forward, rewritten against the new endpoints)
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptCreateTests.cs` (new)

**Interfaces:**
- Consumes: `IDeliveryReceiptService` (Task 8), `DeliveryReceipt.Create`/`AddItem`/Task 2-3 shape, `SaleItem.DeliveryRequiredQuantity` (Task 1).
- Produces: `CreateAsync`, `CreateBatchAsync`, `ListForSaleAsync`, `GetAsync`, `GetSaleFulfillmentAsync` — fully implemented. `MarkDeliveredAsync`/`CancelAsync` are stubbed with `throw new NotImplementedException()` in this task (Task 10 replaces just those two method bodies) — every other member is final. Produces the private `MapToDtoAsync` and `ComputeAvailabilityAsync` helpers Task 10 reuses.

- [ ] **Step 1: Write the failing tests**

Create `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptCreateTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Application.Sales;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptCreateTests : IntegrationTest
{
    public DeliveryReceiptCreateTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, string SaleNumber, Guid SaleItemId, decimal DeliveryRequiredQuantity);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    /// <summary>One completed sale with <paramref name="deliveryRequiredQuantity"/> of its single line
    /// marked for delivery; the rest stays Take-now.</summary>
    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal deliveryRequiredQuantity = 6m, decimal price = 100m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null, deliveryRequiredQuantity) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + 500m) }));

        return new Scene(sale.SaleId, sale.SaleNumber, sale.Items[0].SaleItemId, deliveryRequiredQuantity);
    }

    private static CreateDeliveryReceiptRequest Req(Scene s, decimal quantity, DateOnly? date = null) => new(
        date ?? Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", "0917 111 2222", "Leave at guardhouse",
        new[] { new CreateDeliveryReceiptItemInput(s.SaleItemId, quantity) });

    [Fact]
    public async Task Create_persists_a_pending_schedule_with_only_its_own_assigned_items()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dr = (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        dr.SequenceNumber.Should().Be(1);
        dr.Status.Should().Be(DeliveryStatus.Pending);
        dr.ScheduledDeliveryDate.Should().Be(Today);
        dr.RelatedSaleNumber.Should().Be(scene.SaleNumber);
        dr.Items.Should().ContainSingle();
        dr.Items[0].SaleItemId.Should().Be(scene.SaleItemId);
        dr.Items[0].Quantity.Should().Be(4m); // not the full sale quantity — a partial delivery
    }

    [Fact]
    public async Task Second_schedule_for_the_same_sale_gets_sequence_number_two()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));

        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 2m));
        var dr = (await second.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        dr.SequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Create_rejects_a_quantity_exceeding_what_remains_available_to_schedule()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m)); // 2 left available

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 3m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_scheduling_a_quantity_that_was_never_marked_for_delivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m); // entirely Take-now

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 1m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_blank_recipient_name_or_address()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "  ", "123 Ayala Ave", null, null,
                new[] { new CreateDeliveryReceiptItemInput(scene.SaleItemId, 1m) })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_an_empty_item_list()
    {
        var scene = await ArrangeSaleAsync();
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan", "123 Ayala Ave", null, null,
                Array.Empty<CreateDeliveryReceiptItemInput>()));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_duplicate_sale_item_within_one_delivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan", "123 Ayala Ave", null, null, new[]
            {
                new CreateDeliveryReceiptItemInput(scene.SaleItemId, 2m),
                new CreateDeliveryReceiptItemInput(scene.SaleItemId, 2m),
            }));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_rejects_a_scheduled_date_before_business_local_today()
    {
        var scene = await ArrangeSaleAsync();
        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts",
            Req(scene, 1m, Today.AddDays(-1)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_is_rejected_for_a_voided_sale()
    {
        var scene = await ArrangeSaleAsync();
        (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/void", new VoidSaleRequest("test"))).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 1m));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Batch_creates_every_schedule_with_ascending_sequence_numbers()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var batchId = Guid.NewGuid();

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch",
            new CreateDeliveryReceiptBatchRequest(batchId, new[]
            {
                Req(scene, 4m),
                Req(scene, 2m, Today.AddDays(1)),
            }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = (await response.Content.ReadFromJsonAsync<DeliveryReceiptBatchResultDto>(TestJson.Options))!;

        result.WasExistingBatch.Should().BeFalse();
        result.Created.Should().HaveCount(2);
        result.Created.Select(d => d.SequenceNumber).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public async Task Batch_rejects_two_schedules_that_together_over_allocate_the_same_sale_item()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);

        var response = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch",
            new CreateDeliveryReceiptBatchRequest(Guid.NewGuid(), new[] { Req(scene, 4m), Req(scene, 3m) })); // 4+3 > 6

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(0); // nothing partially applied
            return true;
        });
    }

    [Fact]
    public async Task Retrying_the_same_BatchRequestId_returns_the_original_rows_without_duplicating()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var request = new CreateDeliveryReceiptBatchRequest(Guid.NewGuid(), new[] { Req(scene, 4m) });

        var first = await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch", request))
            .Content.ReadFromJsonAsync<DeliveryReceiptBatchResultDto>(TestJson.Options);
        var second = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts/batch", request);
        var secondDto = (await second.Content.ReadFromJsonAsync<DeliveryReceiptBatchResultDto>(TestJson.Options))!;

        secondDto.WasExistingBatch.Should().BeTrue();
        secondDto.Created.Single().Id.Should().Be(first!.Created.Single().Id);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(1);
            return true;
        });
    }

    [Fact]
    public async Task List_for_sale_returns_every_schedule_ordered_by_sequence_number()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 2m));

        var list = await Client.GetFromJsonAsync<List<DeliveryReceiptDto>>(
            $"/api/sales/{scene.SaleId}/delivery-receipts", TestJson.Options);

        list.Should().HaveCount(2);
        list![0].SequenceNumber.Should().Be(1);
        list[1].SequenceNumber.Should().Be(2);
    }

    [Fact]
    public async Task Get_by_id_returns_a_stable_snapshot_after_a_catalog_rename()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var dr = (await (await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m)))
            .Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        var productName = dr.Items[0].ProductName;
        await InScopeAsync(async db =>
        {
            var saleItem = await db.SaleItems.SingleAsync(i => i.Id == scene.SaleItemId);
            var product = await db.Products.SingleAsync(p => p.Id == db.ProductVariants.Where(v => v.Id == saleItem.ProductVariantId).Select(v => v.ProductId).Single());
            product.Rename("Totally Different Name");
            await db.SaveChangesAsync();
            return true;
        });

        var again = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        again!.Items[0].ProductName.Should().Be(productName);
    }

    [Fact]
    public async Task Fulfillment_summary_reflects_partial_scheduling_and_gates_CanCreateDelivery()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 4m));

        var summary = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>(
            $"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PartiallyScheduled);
        summary.CanCreateDelivery.Should().BeTrue(); // 2 units still available
        summary.Items[0].PendingQuantity.Should().Be(4m);
        summary.Items[0].AvailableToScheduleQuantity.Should().Be(2m);

        await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", Req(scene, 2m));
        var full = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>(
            $"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);
        full!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.FullyScheduled);
        full.CanCreateDelivery.Should().BeFalse();
    }

    [Fact]
    public async Task A_sale_with_nothing_marked_for_delivery_reports_NotApplicable()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 0m);

        var summary = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>(
            $"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);

        summary!.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.NotApplicable);
        summary.CanCreateDelivery.Should().BeFalse();
    }
}
```

If `Product` has no existing `Rename(...)` domain method, use the same raw-SQL rename the old test used instead: `await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Products SET Name = {"Totally Different Name"} WHERE Id = {productId}")` — check `src/Negosio.Domain/Entities/Catalog/Product.cs` first and use whichever actually exists; do not guess.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptCreateTests`
Expected: FAIL to compile (the service doesn't implement the new interface yet — this is the continuation of Task 8's expected break).

- [ ] **Step 3: Create the shared fulfillment-status calculator**

This is used by both this service and Task 11's `ReportsService.GetDeliveryFulfillmentAsync` — extracted to its own file up front so the derivation logic exists in exactly one place, never copy-pasted.

`src/Negosio.Application/Delivery/SaleFulfillmentCalculator.cs`:

```csharp
namespace Negosio.Application.Delivery;

/// <summary>
/// Derives a <see cref="SaleFulfillmentStatus"/> from a sale's aggregate delivery figures. Priority-
/// ordered — the first matching rule wins. Pure and stateless: both <see cref="DeliveryReceiptService"/>
/// (the Sale-detail fulfillment summary) and <see cref="Reports.ReportsService"/> (the Sale fulfillment
/// report view) call this so the two views can never disagree about what a sale's status is.
/// </summary>
public static class SaleFulfillmentCalculator
{
    public static SaleFulfillmentStatus Derive(
        decimal totalDeliveryRequiredQuantity,
        decimal totalPendingQuantity,
        decimal totalDeliveredQuantity,
        decimal totalAvailableToScheduleQuantity,
        bool hasOverduePendingSchedule)
    {
        if (totalDeliveryRequiredQuantity == 0m) return SaleFulfillmentStatus.NotApplicable;
        if (totalDeliveredQuantity >= totalDeliveryRequiredQuantity) return SaleFulfillmentStatus.FullyDelivered;
        if (totalDeliveredQuantity > 0m) return SaleFulfillmentStatus.PartiallyDelivered;
        if (hasOverduePendingSchedule) return SaleFulfillmentStatus.NeedsRescheduling;
        if (totalAvailableToScheduleQuantity == 0m) return SaleFulfillmentStatus.FullyScheduled;
        if (totalPendingQuantity > 0m) return SaleFulfillmentStatus.PartiallyScheduled;
        return SaleFulfillmentStatus.Unscheduled;
    }
}
```

- [ ] **Step 4: Replace `DeliveryReceiptService.cs` in full**

```csharp
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Branches;
using Negosio.Application.Common;
using Negosio.Application.Reports;
using Negosio.Application.Settings;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Delivery;

/// <summary>
/// Schedules, tracks and reports on one or more <see cref="DeliveryReceipt"/> records per
/// <see cref="Sale"/>. Every sold quantity is Take-now unless a <c>SaleItem.DeliveryRequiredQuantity</c>
/// was set at checkout; this service allocates that delivery-required portion across one or more dated,
/// partial delivery schedules. See the plan's Global Constraints for the concurrency and idempotency
/// strategy this class implements.
/// </summary>
public sealed class DeliveryReceiptService : IDeliveryReceiptService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<CreateDeliveryReceiptRequest> _createValidator;
    private readonly IValidator<CreateDeliveryReceiptBatchRequest> _batchValidator;
    private readonly IValidator<CancelDeliveryReceiptRequest> _cancelValidator;
    private readonly IBranchAccessResolver _branchAccess;
    private readonly IReceiptSettingsResolver _settingsResolver;
    private readonly TimeProvider _timeProvider;

    public DeliveryReceiptService(
        ITenantDbContext db,
        ICurrentUser currentUser,
        IValidator<CreateDeliveryReceiptRequest> createValidator,
        IValidator<CreateDeliveryReceiptBatchRequest> batchValidator,
        IValidator<CancelDeliveryReceiptRequest> cancelValidator,
        IBranchAccessResolver branchAccess,
        IReceiptSettingsResolver settingsResolver,
        TimeProvider timeProvider)
    {
        _db = db;
        _currentUser = currentUser;
        _createValidator = createValidator;
        _batchValidator = batchValidator;
        _cancelValidator = cancelValidator;
        _branchAccess = branchAccess;
        _settingsResolver = settingsResolver;
        _timeProvider = timeProvider;
    }

    public async Task<DeliveryReceiptDto> CreateAsync(Guid saleId, CreateDeliveryReceiptRequest request, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        await _createValidator.ValidateAndThrowAppAsync(request, ct);

        var sale = await LoadDeliverableSaleAsync(tenantId, saleId, ct);
        EnsureNotPastBusinessToday(request.ScheduledDeliveryDate);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await LockSaleItemsAsync(tenantId, sale.Id, ct);

        var availability = await ComputeAvailabilityAsync(tenantId, sale, ct);
        var lines = ValidateAndResolveLines(sale, availability, request.Items);

        var preparedByName = await ResolvePreparedByNameAsync(ct);
        var sequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, ct);

        var dr = DeliveryReceipt.Create(
            tenantId, sale.BranchId, sale.Id, sale.SaleNumber, sequenceNumber, request.ScheduledDeliveryDate,
            request.RecipientName, request.DeliveryAddress, request.ContactNumber, request.DeliveryNotes,
            _currentUser.UserId, preparedByName);

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
                "Another delivery was created for this sale at the same time. Please retry.");
        }

        await transaction.CommitAsync(ct);
        return await MapToDtoAsync(dr, ct);
    }

    public async Task<DeliveryReceiptBatchResultDto> CreateBatchAsync(
        Guid saleId, CreateDeliveryReceiptBatchRequest request, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        await _batchValidator.ValidateAndThrowAppAsync(request, ct);

        // Idempotency pre-check — a repeat of the same batch returns the same rows (see Global Constraints).
        var existingBatch = await _db.DeliveryReceipts.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.BatchRequestId == request.BatchRequestId)
            .OrderBy(d => d.SequenceNumber)
            .Include(d => d.Items)
            .ToListAsync(ct);
        if (existingBatch.Count > 0)
        {
            var existingDtos = new List<DeliveryReceiptDto>(existingBatch.Count);
            foreach (var dr in existingBatch)
            {
                existingDtos.Add(await MapToDtoAsync(dr, ct));
            }

            return new DeliveryReceiptBatchResultDto(existingDtos, WasExistingBatch: true);
        }

        var sale = await LoadDeliverableSaleAsync(tenantId, saleId, ct);
        foreach (var schedule in request.Schedules)
        {
            EnsureNotPastBusinessToday(schedule.ScheduledDeliveryDate);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await LockSaleItemsAsync(tenantId, sale.Id, ct);

        // One availability snapshot for the whole batch, decremented in-memory as each schedule is
        // resolved in order — this is what stops two schedules in the SAME batch from double-claiming
        // the same units (a per-schedule-only check would miss that, since neither schedule alone
        // exceeds availability).
        var availability = await ComputeAvailabilityAsync(tenantId, sale, ct);
        var preparedByName = await ResolvePreparedByNameAsync(ct);
        var nextSequenceNumber = await NextSequenceNumberAsync(tenantId, saleId, ct);

        var created = new List<DeliveryReceipt>(request.Schedules.Count);
        foreach (var schedule in request.Schedules)
        {
            var lines = ValidateAndResolveLines(sale, availability, schedule.Items);

            var dr = DeliveryReceipt.Create(
                tenantId, sale.BranchId, sale.Id, sale.SaleNumber, nextSequenceNumber++, schedule.ScheduledDeliveryDate,
                schedule.RecipientName, schedule.DeliveryAddress, schedule.ContactNumber, schedule.DeliveryNotes,
                _currentUser.UserId, preparedByName, request.BatchRequestId);

            foreach (var (saleItem, quantity) in lines)
            {
                dr.AddItem(saleItem.Id, saleItem.ProductNameSnapshot, saleItem.VariantNameSnapshot, quantity, saleItem.UnitPrice);
                availability[saleItem.Id] -= quantity; // consume for the remaining schedules in this batch
            }

            _db.DeliveryReceipts.Add(dr);
            created.Add(dr);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (SqlUniqueViolation.TryGetConstraintName(ex, out var name))
        {
            await transaction.RollbackAsync(ct);

            if (name?.Contains("BatchRequestId", StringComparison.OrdinalIgnoreCase) == true)
            {
                var winner = await _db.DeliveryReceipts.AsNoTracking()
                    .Where(d => d.TenantId == tenantId && d.BatchRequestId == request.BatchRequestId)
                    .OrderBy(d => d.SequenceNumber)
                    .Include(d => d.Items)
                    .ToListAsync(ct);
                var winnerDtos = new List<DeliveryReceiptDto>(winner.Count);
                foreach (var dr in winner)
                {
                    winnerDtos.Add(await MapToDtoAsync(dr, ct));
                }

                return new DeliveryReceiptBatchResultDto(winnerDtos, WasExistingBatch: true);
            }

            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "Another delivery was created for this sale at the same time. Please retry.");
        }

        await transaction.CommitAsync(ct);

        var createdDtos = new List<DeliveryReceiptDto>(created.Count);
        foreach (var dr in created)
        {
            createdDtos.Add(await MapToDtoAsync(dr, ct));
        }

        return new DeliveryReceiptBatchResultDto(createdDtos, WasExistingBatch: false);
    }

    public async Task<IReadOnlyList<DeliveryReceiptDto>> ListForSaleAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var sale = await _db.Sales.AsNoTracking()
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        var receipts = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .OrderBy(d => d.SequenceNumber)
            .ToListAsync(ct);

        var dtos = new List<DeliveryReceiptDto>(receipts.Count);
        foreach (var dr in receipts)
        {
            dtos.Add(await MapToDtoAsync(dr, ct));
        }

        return dtos;
    }

    public async Task<DeliveryReceiptDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var dr = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.");

        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.", ct);
        return await MapToDtoAsync(dr, ct);
    }

    public Task<DeliveryReceiptDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public Task<DeliveryReceiptDto> CancelAsync(Guid id, CancelDeliveryReceiptRequest request, CancellationToken ct = default) =>
        throw new NotImplementedException("Implemented in Task 10.");

    public async Task<SaleDeliverySummaryDto> GetSaleFulfillmentAsync(Guid saleId, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");
        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        var availability = await ComputeAvailabilityAsync(tenantId, sale, ct);

        var itemDtos = sale.Items
            .Where(i => i.DeliveryRequiredQuantity > 0m)
            .OrderBy(i => i.CreatedAtUtc)
            .Select(i =>
            {
                var (pending, delivered) = availability.Raw[i.Id];
                return new SaleItemFulfillmentDto(
                    i.Id, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, i.TakeNowQuantity,
                    i.DeliveryRequiredQuantity, pending, delivered, availability[i.Id]);
            })
            .ToList();

        var receipts = await _db.DeliveryReceipts.AsNoTracking()
            .Include(d => d.Items)
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .OrderBy(d => d.SequenceNumber)
            .ToListAsync(ct);

        var deliveryDtos = new List<DeliveryReceiptSummaryDto>(receipts.Count);
        foreach (var dr in receipts)
        {
            var itemsForDr = await MapItemsAsync(dr, ct);
            deliveryDtos.Add(new DeliveryReceiptSummaryDto(
                dr.Id, dr.SequenceNumber, dr.ScheduledDeliveryDate, dr.Status, dr.RecipientName, dr.DeliveryAddress,
                dr.ContactNumber, dr.DeliveryNotes, dr.DeliveredAtUtc, dr.CancelledAtUtc, dr.CancellationReason, itemsForDr));
        }

        var totalRequired = itemDtos.Sum(i => i.DeliveryRequiredQuantity);
        var totalPending = itemDtos.Sum(i => i.PendingQuantity);
        var totalDelivered = itemDtos.Sum(i => i.DeliveredQuantity);
        var totalAvailable = itemDtos.Sum(i => i.AvailableToScheduleQuantity);
        var todayLocal = BusinessToday();
        var hasOverduePending = receipts.Any(d => d.Status == DeliveryStatus.Pending && d.ScheduledDeliveryDate < todayLocal);

        var status = SaleFulfillmentCalculator.Derive(totalRequired, totalPending, totalDelivered, totalAvailable, hasOverduePending);

        return new SaleDeliverySummaryDto(
            sale.Id, status, sale.DeliveryCharge, itemDtos, deliveryDtos, CanCreateDelivery: totalAvailable > 0m);
    }

    // ---- shared helpers (also used by Task 10's MarkDeliveredAsync/CancelAsync) ----

    /// <summary>Per-SaleItem (Pending, Delivered) totals, indexable both as the raw tuple
    /// (<see cref="Raw"/>) and as the derived AvailableToScheduleQuantity via the indexer.</summary>
    private sealed class AvailabilityMap
    {
        private readonly Dictionary<Guid, decimal> _deliveryRequired;
        public Dictionary<Guid, (decimal Pending, decimal Delivered)> Raw { get; }

        public AvailabilityMap(Dictionary<Guid, decimal> deliveryRequired, Dictionary<Guid, (decimal, decimal)> raw)
        {
            _deliveryRequired = deliveryRequired;
            Raw = raw;
        }

        public decimal this[Guid saleItemId]
        {
            get
            {
                var (pending, delivered) = Raw.TryGetValue(saleItemId, out var v) ? v : (0m, 0m);
                return _deliveryRequired.GetValueOrDefault(saleItemId) - pending - delivered;
            }
            set
            {
                // Only ever decremented in-memory during CreateBatchAsync — reassigns Pending so the
                // indexer's getter reflects the consumed amount for the NEXT schedule in the same batch.
                var deliveryRequired = _deliveryRequired.GetValueOrDefault(saleItemId);
                var (_, delivered) = Raw.TryGetValue(saleItemId, out var existing) ? existing : (0m, 0m);
                var impliedPending = deliveryRequired - delivered - value;
                Raw[saleItemId] = (impliedPending, delivered);
            }
        }
    }

    private async Task<AvailabilityMap> ComputeAvailabilityAsync(Guid tenantId, Sale sale, CancellationToken ct)
    {
        var saleItemIds = sale.Items.Select(i => i.Id).ToList();
        var allocations = await (
            from i in _db.DeliveryReceiptItems.AsNoTracking()
            join d in _db.DeliveryReceipts.AsNoTracking() on i.DeliveryReceiptId equals d.Id
            where i.TenantId == tenantId && saleItemIds.Contains(i.SaleItemId) && d.Status != DeliveryStatus.Cancelled
            select new { i.SaleItemId, i.Quantity, d.Status })
            .ToListAsync(ct);

        var raw = allocations
            .GroupBy(a => a.SaleItemId)
            .ToDictionary(
                g => g.Key,
                g => (
                    Pending: g.Where(a => a.Status == DeliveryStatus.Pending).Sum(a => a.Quantity),
                    Delivered: g.Where(a => a.Status == DeliveryStatus.Delivered).Sum(a => a.Quantity)));

        var deliveryRequired = sale.Items.ToDictionary(i => i.Id, i => i.DeliveryRequiredQuantity);
        return new AvailabilityMap(deliveryRequired, raw);
    }

    /// <summary>
    /// Validates and resolves one delivery's requested lines against the (possibly already
    /// batch-decremented) <paramref name="availability"/> map — never trusts the frontend's own
    /// available-quantity math. Every SaleItemId must belong to <paramref name="sale"/> (same tenant
    /// and sale by construction, since <paramref name="sale"/>.Items is already tenant/sale-scoped).
    /// </summary>
    private static IReadOnlyList<(SaleItem SaleItem, decimal Quantity)> ValidateAndResolveLines(
        Sale sale, AvailabilityMap availability, IReadOnlyList<CreateDeliveryReceiptItemInput> items)
    {
        var pairs = new List<(SaleItem, decimal)>(items.Count);
        foreach (var line in items)
        {
            var saleItem = sale.Items.SingleOrDefault(i => i.Id == line.SaleItemId)
                ?? throw new BusinessRuleException(ErrorCodes.InvalidSaleItem, "A delivery line refers to an item that is not on this sale.");

            if (line.Quantity > availability[saleItem.Id])
            {
                throw new BusinessRuleException(
                    ErrorCodes.DeliveryQuantityExceedsAvailable,
                    $"Only {availability[saleItem.Id]} of \"{saleItem.ProductNameSnapshot}\" is still available to schedule.");
            }

            pairs.Add((saleItem, line.Quantity));
        }

        return pairs;
    }

    private async Task<Sale> LoadDeliverableSaleAsync(Guid tenantId, Guid saleId, CancellationToken ct)
    {
        var sale = await _db.Sales.AsNoTracking()
            .Include(s => s.Items)
            .SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == saleId, ct)
            ?? throw new NotFoundException(ErrorCodes.SaleNotFound, "Sale not found.");

        await GuardBranchAsync(sale.BranchId, ErrorCodes.SaleNotFound, "Sale not found.", ct);

        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded or SaleStatus.Refunded))
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryReceiptNotAllowed, "A delivery can only be scheduled for a completed sale.");
        }

        return sale;
    }

    /// <summary>
    /// Serializes concurrent allocation attempts against the same Sale's items. RowVersion (this
    /// codebase's default optimistic-concurrency idiom) only protects an UPDATE against a row that
    /// already exists — it cannot catch two concurrent CreateAsync/CreateBatchAsync calls each
    /// INSERTing a brand-new DeliveryReceiptItem, since neither call updates a shared row. Without
    /// this lock, two transactions under SQL Server's default READ COMMITTED isolation could both read
    /// the same "4 available" snapshot before either commits, and both insert — over-allocating past
    /// what was ever marked for delivery. <c>WITH (UPDLOCK, HOLDLOCK)</c> is the one pessimistic-lock
    /// precedent already in this codebase (<c>VoidSaleService</c>, for a different cross-aggregate
    /// race) — reused here for the same reason: it is the right tool exactly when RowVersion structurally
    /// cannot apply. The lock is held until the transaction commits or rolls back, so a second
    /// concurrent call blocks here until the first finishes, then re-reads a availability snapshot that
    /// correctly reflects what the first call just consumed.
    /// </summary>
    private async Task LockSaleItemsAsync(Guid tenantId, Guid saleId, CancellationToken ct) =>
        await _db.SaleItems
            .FromSqlInterpolated($"SELECT * FROM SaleItems WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = {tenantId} AND SaleId = {saleId}")
            .AsNoTracking()
            .ToListAsync(ct);

    private async Task<int> NextSequenceNumberAsync(Guid tenantId, Guid saleId, CancellationToken ct)
    {
        var max = await _db.DeliveryReceipts
            .Where(d => d.TenantId == tenantId && d.SaleId == saleId)
            .Select(d => (int?)d.SequenceNumber)
            .MaxAsync(ct);
        return (max ?? 0) + 1;
    }

    private DateOnly BusinessToday() =>
        DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime + ReportPeriodResolver.BusinessOffset);

    private void EnsureNotPastBusinessToday(DateOnly scheduledDate)
    {
        if (scheduledDate < BusinessToday())
        {
            throw new BusinessRuleException(
                ErrorCodes.DeliveryScheduleDateInPast, "The scheduled delivery date cannot be in the past.");
        }
    }

    private async Task<string> ResolvePreparedByNameAsync(CancellationToken ct) =>
        await _db.Users.Where(u => u.Id == _currentUser.UserId)
            .Select(u => u.FirstName + " " + u.LastName)
            .FirstOrDefaultAsync(ct) ?? string.Empty;

    private async Task<IReadOnlyList<DeliveryReceiptItemDto>> MapItemsAsync(DeliveryReceipt dr, CancellationToken ct)
    {
        var settings = await _settingsResolver.ResolveAsync(dr.BranchId, ct);
        return dr.Items
            .OrderBy(i => i.CreatedAtUtc)
            .ThenBy(i => i.Id)
            .Select(i =>
            {
                decimal? unitPrice = settings.DeliveryShowPrices ? i.UnitPrice : null;
                decimal? amount = settings.DeliveryShowPrices && i.UnitPrice is { } price
                    ? Money.Round(i.Quantity * price)
                    : null;
                return new DeliveryReceiptItemDto(i.SaleItemId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, unitPrice, amount);
            })
            .ToList();
    }

    /// <summary>Shared DTO builder for every read path (Create/CreateBatch's response, Get, List, and
    /// — once Task 10 lands — MarkDelivered/Cancel's response). Only <see cref="DeliveryReceipt.Items"/>
    /// needs to already be loaded on <paramref name="dr"/>; everything else (branch/business/settings,
    /// live DeliveryCharge, the delivered/cancelled actor names) is resolved here.</summary>
    private async Task<DeliveryReceiptDto> MapToDtoAsync(DeliveryReceipt dr, CancellationToken ct)
    {
        var settings = await _settingsResolver.ResolveAsync(dr.BranchId, ct);

        var profile = await _db.TenantProfiles.Where(p => p.Id == dr.TenantId)
            .Select(p => new { p.Name, p.ContactNumber, p.TaxId })
            .SingleAsync(ct);
        var branch = await _db.Branches.Where(b => b.Id == dr.BranchId)
            .Select(b => new { b.Name, b.AddressLine1, b.City, b.Province, b.ContactNumber })
            .FirstOrDefaultAsync(ct);

        var deliveryCharge = dr.SaleId is { } saleId
            ? await _db.Sales.AsNoTracking().Where(s => s.Id == saleId).Select(s => s.DeliveryCharge).FirstOrDefaultAsync(ct)
            : 0m;

        var actorIds = new[] { dr.DeliveredByUserId, dr.CancelledByUserId }.Where(id => id is not null).Select(id => id!.Value).ToList();
        var actorNames = actorIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _db.Users.AsNoTracking().Where(u => actorIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);

        var addressParts = new[] { branch?.AddressLine1, branch?.City, branch?.Province }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim())
            .ToArray();
        var businessAddress = addressParts.Length > 0 ? string.Join(", ", addressParts) : null;

        var items = dr.Items
            .OrderBy(i => i.CreatedAtUtc)
            .ThenBy(i => i.Id)
            .Select(i =>
            {
                decimal? unitPrice = settings.DeliveryShowPrices ? i.UnitPrice : null;
                decimal? amount = settings.DeliveryShowPrices && i.UnitPrice is { } price
                    ? Money.Round(i.Quantity * price)
                    : null;
                return new DeliveryReceiptItemDto(i.SaleItemId, i.ProductNameSnapshot, i.VariantNameSnapshot, i.Quantity, unitPrice, amount);
            })
            .ToList();

        return new DeliveryReceiptDto(
            dr.Id, dr.SaleId, dr.RelatedSaleNumber, dr.SequenceNumber, dr.ScheduledDeliveryDate, dr.Status, dr.CreatedAtUtc,
            branch?.Name ?? string.Empty, dr.RecipientName, dr.DeliveryAddress, dr.ContactNumber, dr.DeliveryNotes,
            dr.PreparedByNameSnapshot,
            dr.DeliveredAtUtc, dr.DeliveredByUserId is { } dbu ? actorNames.GetValueOrDefault(dbu) : null,
            dr.CancelledAtUtc, dr.CancelledByUserId is { } cbu ? actorNames.GetValueOrDefault(cbu) : null, dr.CancellationReason,
            items, deliveryCharge,
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
    }

    /// <summary>Branch-scoped users may only see their own branch's documents (404, not 403). Owner/Admin
    /// are unrestricted.</summary>
    private async Task GuardBranchAsync(Guid branchId, string code, string message, CancellationToken ct)
    {
        var assigned = await _branchAccess.AssignedBranchIdAsync(ct);
        if (assigned is { } scoped && scoped != branchId)
        {
            throw new NotFoundException(code, message);
        }
    }

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
```

`AvailabilityMap`'s setter is only exercised by `CreateBatchAsync`'s in-batch decrement — it re-derives an implied Pending figure purely so the indexer getter reflects consumption for later schedules in the same request; it is never persisted and never read via `.Raw` after being set this way (only `GetSaleFulfillmentAsync`, which never calls the setter, reads `.Raw` for its Pending/Delivered breakdown). Read it once fully before implementing — this is the one deliberately unusual piece of this task.

- [ ] **Step 5: Wire the new routes into `SalesController`**

Replace the "Delivery receipts" region (the old `GetDeliveryReceipt`/`CreateDeliveryReceipt` actions) with:

```csharp
    // Delivery fulfillment — class-level SalesView is the gate for view/create (any sales-viewing
    // role); DeliveryReceiptsController.Cancel below is the only action with a narrower policy.
    [HttpGet("{id:guid}/delivery-receipts")]
    [ProducesResponseType(typeof(IReadOnlyList<DeliveryReceiptDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DeliveryReceiptDto>>> ListDeliveryReceipts(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.ListForSaleAsync(id, ct));

    [HttpGet("{id:guid}/delivery-summary")]
    [ProducesResponseType(typeof(SaleDeliverySummaryDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SaleDeliverySummaryDto>> DeliverySummary(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.GetSaleFulfillmentAsync(id, ct));

    [HttpPost("{id:guid}/delivery-receipts")]
    [ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<DeliveryReceiptDto>> CreateDeliveryReceipt(
        Guid id, [FromBody] CreateDeliveryReceiptRequest request, CancellationToken ct)
    {
        var created = await _deliveryReceipts.CreateAsync(id, request, ct);
        return Created($"/api/delivery-receipts/{created.Id}", created);
    }

    [HttpPost("{id:guid}/delivery-receipts/batch")]
    [ProducesResponseType(typeof(DeliveryReceiptBatchResultDto), StatusCodes.Status201Created)]
    public async Task<ActionResult<DeliveryReceiptBatchResultDto>> CreateDeliveryReceiptBatch(
        Guid id, [FromBody] CreateDeliveryReceiptBatchRequest request, CancellationToken ct)
        => Created(string.Empty, await _deliveryReceipts.CreateBatchAsync(id, request, ct));
```

Remove the old `GetDeliveryReceipt`/`CreateDeliveryReceipt` actions (the ones routed at `{id:guid}/delivery-receipt`, singular) entirely — they're superseded by the four actions above.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptCreateTests`
Expected: PASS. Also run `dotnet build Negosio.sln` — it should now build cleanly except for `DeliveryReceiptsController.cs`, which still references nothing broken (its only action, `Get`, is unaffected) — confirm no remaining compile errors anywhere.

- [ ] **Step 7: Delete the superseded test file and commit**

```bash
git rm tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptTests.cs
git add src/Negosio.Application/Delivery/SaleFulfillmentCalculator.cs src/Negosio.Application/Delivery/DeliveryReceiptService.cs src/Negosio.Api/Controllers/SalesController.cs tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptCreateTests.cs
git commit -m "feat(delivery): scheduled, partial, multi-delivery creation with idempotent batching"
```

---

### Task 10: Mark delivered / cancel — status transitions with RowVersion concurrency

**Files:**
- Modify: `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` (replace only the two `NotImplementedException` stub bodies)
- Modify: `src/Negosio.Api/Controllers/DeliveryReceiptsController.cs`
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptStatusTests.cs` (new)

**Interfaces:**
- Consumes: `DeliveryReceipt.MarkDelivered(Guid, DateTime)`/`.Cancel(Guid, string, DateTime)` (Task 2), `MapToDtoAsync`/`GuardBranchAsync`/`RequireTenant` (Task 9).
- Produces: `MarkDeliveredAsync`/`CancelAsync` fully implemented — `IDeliveryReceiptService` has no remaining stub. Task 12's concurrency tests dispatch against these two methods.

- [ ] **Step 1: Write the failing tests**

Create `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptStatusTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptStatusTests : IntegrationTest
{
    public DeliveryReceiptStatusTests(NegosioApiFactory factory) : base(factory) { }

    private sealed record Scene(Guid SaleId, Guid BranchId, Guid SaleItemId);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal deliveryRequiredQuantity = 6m, decimal price = 100m)
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: price, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null, deliveryRequiredQuantity) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: (price * qty) + 500m) }));

        return new Scene(sale.SaleId, branchId, sale.Items[0].SaleItemId);
    }

    private async Task<DeliveryReceiptDto> CreateDeliveryAsync(Scene s, decimal quantity)
    {
        var response = await Client.PostAsJsonAsync($"/api/sales/{s.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
                new[] { new CreateDeliveryReceiptItemInput(s.SaleItemId, quantity) }));
        return (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
    }

    [Fact]
    public async Task Mark_delivered_transitions_pending_to_delivered_and_stamps_audit_fields()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);

        var response = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var delivered = (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        delivered.Status.Should().Be(DeliveryStatus.Delivered);
        delivered.DeliveredAtUtc.Should().NotBeNull();
        delivered.DeliveredByName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mark_delivered_twice_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_requires_a_non_blank_reason()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("   "));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cancel_transitions_pending_to_cancelled_and_releases_quantity_to_unscheduled()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var dr = await CreateDeliveryAsync(scene, 4m); // 2 left available

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("Customer rescheduled"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = (await response.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        cancelled.Status.Should().Be(DeliveryStatus.Cancelled);
        cancelled.CancellationReason.Should().Be("Customer rescheduled");
        cancelled.Items.Should().ContainSingle(); // its own item rows are preserved, unchanged, as history

        var summary = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>($"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);
        summary!.Items[0].AvailableToScheduleQuantity.Should().Be(6m); // fully released
        summary.CanCreateDelivery.Should().BeTrue();
    }

    [Fact]
    public async Task Rescheduling_after_a_cancel_creates_a_new_record_and_never_reactivates_the_cancelled_one()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 6m);
        var first = await CreateDeliveryAsync(scene, 4m);
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel", new CancelDeliveryReceiptRequest("Wrong address"));

        var second = await CreateDeliveryAsync(scene, 4m); // re-schedule the same released quantity

        second.Id.Should().NotBe(first.Id);
        second.SequenceNumber.Should().Be(2);
        second.Status.Should().Be(DeliveryStatus.Pending);

        var reloadedFirst = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{first.Id}", TestJson.Options);
        reloadedFirst!.Status.Should().Be(DeliveryStatus.Cancelled); // untouched
    }

    [Fact]
    public async Task Cancel_after_delivered_is_rejected()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("Too late"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Cashier_may_mark_delivered_but_not_cancel()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        Authorize(await AddTenantUserTokenAsync("cashier@example.com", UserRole.Cashier, scene.BranchId));

        (await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Cashier_cannot_cancel_a_delivery()
    {
        var scene = await ArrangeSaleAsync();
        var dr = await CreateDeliveryAsync(scene, 4m);
        Authorize(await AddTenantUserTokenAsync("cashier2@example.com", UserRole.Cashier, scene.BranchId));

        var response = await Client.PostAsJsonAsync($"/api/delivery-receipts/{dr.Id}/cancel", new CancelDeliveryReceiptRequest("Changed mind"));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptStatusTests`
Expected: FAIL — `MarkDeliveredAsync`/`CancelAsync` throw `NotImplementedException` (500), and the controller has no `/deliver`/`/cancel` routes yet (404).

- [ ] **Step 3: Implement the two service methods**

In `DeliveryReceiptService.cs`, replace the two stub lines:

```csharp
    public async Task<DeliveryReceiptDto> MarkDeliveredAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var dr = await _db.DeliveryReceipts.Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.");
        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.", ct);

        if (dr.Status != DeliveryStatus.Pending)
        {
            throw new BusinessRuleException(ErrorCodes.DeliveryReceiptNotPending, "Only a pending delivery can be marked delivered.");
        }

        dr.MarkDelivered(_currentUser.UserId, _timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another status change (a race from a second tab/user) committed between our load and
            // our write — roll back, then report a conflict rather than silently overwriting it.
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This delivery was changed by someone else. Please refresh and try again.");
        }

        await transaction.CommitAsync(ct);
        return await MapToDtoAsync(dr, ct);
    }

    public async Task<DeliveryReceiptDto> CancelAsync(Guid id, CancelDeliveryReceiptRequest request, CancellationToken ct = default)
    {
        var tenantId = RequireTenant();
        await _cancelValidator.ValidateAndThrowAppAsync(request, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var dr = await _db.DeliveryReceipts.Include(d => d.Items)
            .SingleOrDefaultAsync(d => d.TenantId == tenantId && d.Id == id, ct)
            ?? throw new NotFoundException(ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.");
        await GuardBranchAsync(dr.BranchId, ErrorCodes.DeliveryReceiptNotFound, "Delivery receipt not found.", ct);

        if (dr.Status != DeliveryStatus.Pending)
        {
            throw new BusinessRuleException(ErrorCodes.DeliveryReceiptNotPending, "Only a pending delivery can be cancelled.");
        }

        // Cancelling never touches the Sale or its DeliveryCharge, and this DR's own item rows are left
        // exactly as they are — they stay forever as audit history. Quantities are "released" only in
        // the sense that ComputeAvailabilityAsync excludes Cancelled rows from its Pending/Delivered
        // sums, so a future Create call sees them as available again automatically.
        dr.Cancel(_currentUser.UserId, request.Reason, _timeProvider.GetUtcNow().UtcDateTime);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(ct);
            throw new ConflictException(ErrorCodes.DeliveryReceiptConcurrencyConflict,
                "This delivery was changed by someone else. Please refresh and try again.");
        }

        await transaction.CommitAsync(ct);
        return await MapToDtoAsync(dr, ct);
    }
```

- [ ] **Step 4: Wire the controller routes**

In `DeliveryReceiptsController.cs`, add after the existing `Get` action:

```csharp
    [HttpPost("{id:guid}/deliver")]
    [ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryReceiptDto>> MarkDelivered(Guid id, CancellationToken ct)
        => Ok(await _deliveryReceipts.MarkDeliveredAsync(id, ct));

    // Narrower than the class-level SalesView — Owner/Admin/Manager only (see the plan's Global
    // Constraints / recommended authorization levels).
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = AuthorizationPolicies.DeliveryCancel)]
    [ProducesResponseType(typeof(DeliveryReceiptDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryReceiptDto>> Cancel(
        Guid id, [FromBody] CancelDeliveryReceiptRequest request, CancellationToken ct)
        => Ok(await _deliveryReceipts.CancelAsync(id, request, ct));
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter "DeliveryReceiptStatusTests|DeliveryReceiptCreateTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Negosio.Application/Delivery/DeliveryReceiptService.cs src/Negosio.Api/Controllers/DeliveryReceiptsController.cs tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptStatusTests.cs
git commit -m "feat(delivery): mark-delivered and cancel with RowVersion-guarded transitions"
```

---

### Task 11: Reports — fix the distinct-Sale aggregation bug, extend the schedule view, add the Sale fulfillment view

**Files:**
- Modify: `src/Negosio.Application/Reports/ReportsContracts.cs`
- Modify: `src/Negosio.Application/Reports/ReportsService.cs`
- Modify: `src/Negosio.Api/Controllers/ReportsController.cs`
- Modify: `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`

**Interfaces:**
- Consumes: `SaleFulfillmentCalculator.Derive` (Task 9), `DeliveryStatus`/`SaleFulfillmentStatus`.
- Produces: the fixed `GetDeliveriesAsync` (now safe under multi-DR-per-sale) and the new `GetDeliveryFulfillmentAsync` — Task 19 (frontend reports page) consumes both.

- [ ] **Step 1: Write the failing tests**

In `tests/Negosio.IntegrationTests/Reports/ReportsTests.cs`, add (read the file first for its exact scene-building helpers/usings and match its conventions — it already has a `RegisterLoginAndAuthorizeAsync`/`CreateRegisterAsync`/`OpenSessionAsync`/`CreateCategoryAsync`/`SeedStockedProductAsync`/`CheckoutOkAsync` setup pattern identical to every other integration test in this plan):

```csharp
    [Fact]
    public async Task Delivery_report_totals_charge_a_multi_schedule_sale_exactly_once()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null, 6m) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1160m) },
            DeliveryCharge: 60m));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var saleItemId = sale.Items[0].SaleItemId;
        await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null,
                new[] { new CreateDeliveryReceiptItemInput(saleItemId, 4m) }));
        await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null,
                new[] { new CreateDeliveryReceiptItemInput(saleItemId, 2m) }));

        var report = await Client.GetFromJsonAsync<DeliveryReportResultDto>("/api/reports/deliveries", TestJson.Options);

        report!.Page.Items.Should().HaveCount(2); // two schedule rows
        report.Totals.TotalSchedules.Should().Be(2);
        report.Totals.DistinctSalesCount.Should().Be(1);
        report.Totals.TotalDeliveryCharges.Should().Be(60m); // NOT 120m — charged once per sale, not per schedule
        report.Totals.AverageDeliveryChargePerSale.Should().Be(60m);
    }

    [Fact]
    public async Task Delivery_report_filters_by_status()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null, 6m) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1100m) }));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        var drResp = await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null,
                new[] { new CreateDeliveryReceiptItemInput(sale.Items[0].SaleItemId, 4m) }));
        var dr = (await drResp.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        await Client.PostAsync($"/api/delivery-receipts/{dr.Id}/deliver", null);

        var deliveredOnly = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            $"/api/reports/deliveries?Status={(int)DeliveryStatus.Delivered}", TestJson.Options);
        deliveredOnly!.Page.Items.Should().ContainSingle(r => r.DeliveryReceiptId == dr.Id);

        var pendingOnly = await Client.GetFromJsonAsync<DeliveryReportResultDto>(
            $"/api/reports/deliveries?Status={(int)DeliveryStatus.Pending}", TestJson.Options);
        pendingOnly!.Page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Delivery_fulfillment_report_shows_one_row_per_sale_with_aggregated_quantities()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null, 6m) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) }));

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));
        await Client.PostAsJsonAsync($"/api/sales/{sale.SaleId}/delivery-receipts",
            new CreateDeliveryReceiptRequest(today, "Juan", "123 Ayala Ave", null, null,
                new[] { new CreateDeliveryReceiptItemInput(sale.Items[0].SaleItemId, 4m) }));

        var report = await Client.GetFromJsonAsync<DeliveryFulfillmentReportResultDto>(
            "/api/reports/delivery-fulfillment", TestJson.Options);

        var row = report!.Page.Items.Should().ContainSingle(r => r.SaleId == sale.SaleId).Subject;
        row.FulfillmentStatus.Should().Be(SaleFulfillmentStatus.PartiallyScheduled);
        row.TotalDeliveryRequiredQuantity.Should().Be(6m);
        row.TotalPendingQuantity.Should().Be(4m);
        row.TotalUnscheduledQuantity.Should().Be(2m);
        row.ScheduleCount.Should().Be(1);
    }

    [Fact]
    public async Task A_sale_with_nothing_marked_for_delivery_never_appears_in_the_fulfillment_report()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(login);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: 20m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, 10m, null, 0m) }, // nothing for delivery
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 1000m) }));

        var report = await Client.GetFromJsonAsync<DeliveryFulfillmentReportResultDto>(
            "/api/reports/delivery-fulfillment", TestJson.Options);

        report!.Page.Items.Should().NotContain(r => r.SaleId == sale.SaleId);
    }
```

(Add `using Negosio.Application.Delivery;` and `using Negosio.Domain.Enums;` to the file's usings if not already present.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Negosio.IntegrationTests --filter ReportsTests`
Expected: FAIL to compile — `DeliveryReportQuery` has no `Status` query-string-bindable member yet, `DeliveryReportTotalsDto` has no `TotalSchedules`/`DistinctSalesCount`/`AverageDeliveryChargePerSale`, `DeliveryFulfillmentReportResultDto`/`GetDeliveryFulfillmentAsync` don't exist.

- [ ] **Step 3: Extend `ReportsContracts.cs`**

Replace the existing `DeliveryReportQuery`/`DeliveryReportRowDto`/`DeliveryReportTotalsDto` block with:

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
    DeliveryStatus? Status = null,
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
    DeliveryStatus Status,
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
```

Add `Task<DeliveryFulfillmentReportResultDto> GetDeliveryFulfillmentAsync(DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken = default);` to `IReportsService`. Add `using Negosio.Application.Delivery;` to the top of the file (for `DeliveryStatus`... actually that's `Negosio.Domain.Enums` — already imported — but `SaleFulfillmentStatus` lives in `Negosio.Application.Delivery`, so this new `using` is required).

- [ ] **Step 4: Fix and extend `GetDeliveriesAsync`, add `GetDeliveryFulfillmentAsync`**

Replace `GetDeliveriesAsync` and its private `DeliveryReportRow` record in full:

```csharp
    public async Task<DeliveryReportResultDto> GetDeliveriesAsync(DeliveryReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow + ReportPeriodResolver.BusinessOffset);

        var receipts = _db.DeliveryReceipts.AsNoTracking().Where(d => d.TenantId == tenantId && d.SaleId != null);
        if (branchFilter is { } b)
        {
            receipts = receipts.Where(d => d.BranchId == b);
        }

        var (effectiveFrom, effectiveTo, effectiveStatus, overdueOnly) = ResolveDeliveryPreset(query, todayLocal);
        if (effectiveFrom is { } from) receipts = receipts.Where(d => d.ScheduledDeliveryDate >= from);
        if (effectiveTo is { } to) receipts = receipts.Where(d => d.ScheduledDeliveryDate <= to);
        if (effectiveStatus is { } status) receipts = receipts.Where(d => d.Status == status);
        if (overdueOnly) receipts = receipts.Where(d => d.Status == DeliveryStatus.Pending && d.ScheduledDeliveryDate < todayLocal);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            receipts = receipts.Where(d =>
                (d.RelatedSaleNumber != null && d.RelatedSaleNumber.Contains(term)) ||
                d.RecipientName.Contains(term) ||
                d.DeliveryAddress.Contains(term) ||
                (d.ContactNumber != null && d.ContactNumber.Contains(term)));
        }

        var joined =
            from dr in receipts
            join s in _db.Sales.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status != SaleStatus.Voided) on dr.SaleId equals s.Id
            select new { dr, s };

        // Fix: aggregate charge-related totals over DISTINCT sales, never per joined DR row — a sale
        // with N schedules must contribute its DeliveryCharge exactly once (see Global Constraints).
        var distinctSales = joined.Select(x => new { x.s.Id, x.s.DeliveryCharge }).Distinct();
        var distinctSalesCount = await distinctSales.CountAsync(cancellationToken);
        var freeSalesCount = await distinctSales.CountAsync(x => x.DeliveryCharge == 0m, cancellationToken);
        var totalCharges = distinctSalesCount == 0 ? 0m : await distinctSales.SumAsync(x => x.DeliveryCharge, cancellationToken);
        var averageChargePerSale = distinctSalesCount == 0 ? 0m : Money.Round(totalCharges / distinctSalesCount);
        var totalSchedules = await joined.CountAsync(cancellationToken);

        var totals = new DeliveryReportTotalsDto(
            totalSchedules, distinctSalesCount, freeSalesCount, distinctSalesCount - freeSalesCount, totalCharges, averageChargePerSale);

        // Recommended ordering: Pending first, earliest scheduled date, newest-created tiebreak.
        var projected = joined
            .OrderBy(x => x.dr.Status == DeliveryStatus.Pending ? 0 : 1)
            .ThenBy(x => x.dr.ScheduledDeliveryDate)
            .ThenByDescending(x => x.dr.CreatedAtUtc)
            .Select(x => new DeliveryReportRow(
                x.dr.Id, x.s.Id, x.s.SaleNumber, x.dr.SequenceNumber, x.dr.ScheduledDeliveryDate, x.dr.Status,
                x.dr.Status == DeliveryStatus.Pending && x.dr.ScheduledDeliveryDate < todayLocal,
                x.dr.CreatedAtUtc, x.dr.RecipientName, x.dr.DeliveryAddress, x.dr.ContactNumber, x.dr.DeliveryNotes,
                x.s.DeliveryCharge, x.s.GrandTotal, x.dr.PreparedByNameSnapshot,
                x.dr.DeliveredAtUtc, x.dr.CancelledAtUtc, x.dr.CancellationReason,
                _db.Payments.Where(p => p.SaleId == x.s.Id).Select(p => p.Method).Distinct().ToList()));

        var rows = await PagedResult<DeliveryReportRow>.CreateAsync(projected, query.Page, query.PageSize, cancellationToken);
        var items = rows.Items.Select(r => new DeliveryReportRowDto(
            r.DeliveryReceiptId, r.SaleId, r.SaleNumber, r.SequenceNumber, r.ScheduledDeliveryDate, r.Status, r.IsOverdue,
            r.CreatedAtUtc, r.RecipientName, r.DeliveryAddress, r.ContactNumber, r.DeliveryNotes, r.DeliveryCharge, r.SaleGrandTotal,
            string.Join(" + ", r.Methods.Select(m => m.ToString())), r.PreparedByName, r.DeliveredAtUtc, r.CancelledAtUtc, r.CancellationReason))
            .ToList();

        return new DeliveryReportResultDto(
            new PagedResult<DeliveryReportRowDto>(items, rows.Page, rows.PageSize, rows.TotalCount, rows.TotalPages),
            totals);
    }

    private static (DateOnly? From, DateOnly? To, DeliveryStatus? Status, bool OverdueOnly) ResolveDeliveryPreset(
        DeliveryReportQuery query, DateOnly todayLocal) => query.Preset switch
    {
        DeliveryReportPreset.Today => (todayLocal, todayLocal, DeliveryStatus.Pending, false),
        DeliveryReportPreset.Upcoming => (todayLocal.AddDays(1), null, DeliveryStatus.Pending, false),
        DeliveryReportPreset.Overdue => (null, null, null, true),
        DeliveryReportPreset.NeedsRescheduling => (null, null, null, true),
        DeliveryReportPreset.Delivered => (null, null, DeliveryStatus.Delivered, false),
        DeliveryReportPreset.Cancelled => (null, null, DeliveryStatus.Cancelled, false),
        _ => (query.FromDate, query.ToDate, query.Status, false),
    };

    private sealed record DeliveryReportRow(
        Guid DeliveryReceiptId, Guid SaleId, string SaleNumber, int SequenceNumber, DateOnly ScheduledDeliveryDate,
        DeliveryStatus Status, bool IsOverdue, DateTime CreatedAtUtc,
        string RecipientName, string DeliveryAddress, string? ContactNumber, string? DeliveryNotes,
        decimal DeliveryCharge, decimal SaleGrandTotal, string PreparedByName,
        DateTime? DeliveredAtUtc, DateTime? CancelledAtUtc, string? CancellationReason, List<PaymentMethod> Methods);

    public async Task<DeliveryFulfillmentReportResultDto> GetDeliveryFulfillmentAsync(
        DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken = default)
    {
        RequireAuthenticated();
        var tenantId = _currentUser.TenantId;
        var branchFilter = await _branchAccess.ResolveListFilterAsync(query.BranchId, cancellationToken);
        var todayLocal = DateOnly.FromDateTime(DateTime.UtcNow + ReportPeriodResolver.BusinessOffset);

        var qualifyingSales = _db.Sales.AsNoTracking().Where(s => s.TenantId == tenantId && s.Status != SaleStatus.Voided
            && _db.SaleItems.Any(i => i.SaleId == s.Id && i.DeliveryRequiredQuantity > 0m));
        if (branchFilter is { } b)
        {
            qualifyingSales = qualifyingSales.Where(s => s.BranchId == b);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            qualifyingSales = qualifyingSales.Where(s => s.SaleNumber.Contains(term));
        }

        // Aggregated in SQL, but the FulfillmentStatus derivation itself is arithmetic-only and cannot
        // be translated into a single EF-queryable predicate — so it's computed in memory over this
        // already tenant/branch/search-narrowed (delivery-bearing sales only) candidate set, never the
        // whole Sales table. Acceptable for this report's realistic scale; revisit if a tenant's
        // delivery-bearing sale count ever grows large enough for this to matter.
        var rows = await qualifyingSales
            .Select(s => new
            {
                s.Id,
                s.SaleNumber,
                s.CreatedAtUtc,
                s.DeliveryCharge,
                TotalRequired = _db.SaleItems.Where(i => i.SaleId == s.Id).Sum(i => i.DeliveryRequiredQuantity),
                TotalPending = _db.DeliveryReceiptItems.Where(dri => _db.DeliveryReceipts
                        .Any(d => d.Id == dri.DeliveryReceiptId && d.SaleId == s.Id && d.Status == DeliveryStatus.Pending))
                    .Sum(dri => (decimal?)dri.Quantity) ?? 0m,
                TotalDelivered = _db.DeliveryReceiptItems.Where(dri => _db.DeliveryReceipts
                        .Any(d => d.Id == dri.DeliveryReceiptId && d.SaleId == s.Id && d.Status == DeliveryStatus.Delivered))
                    .Sum(dri => (decimal?)dri.Quantity) ?? 0m,
                ScheduleCount = _db.DeliveryReceipts.Count(d => d.SaleId == s.Id),
                HasOverduePending = _db.DeliveryReceipts.Any(d => d.SaleId == s.Id
                    && d.Status == DeliveryStatus.Pending && d.ScheduledDeliveryDate < todayLocal),
            })
            .ToListAsync(cancellationToken);

        var mapped = rows.Select(r =>
        {
            var available = r.TotalRequired - r.TotalPending - r.TotalDelivered;
            var status = SaleFulfillmentCalculator.Derive(r.TotalRequired, r.TotalPending, r.TotalDelivered, available, r.HasOverduePending);
            return new DeliveryFulfillmentReportRowDto(
                r.Id, r.SaleNumber, r.CreatedAtUtc, status, r.DeliveryCharge,
                r.TotalRequired, r.TotalPending, r.TotalDelivered, available, r.ScheduleCount);
        });

        if (query.Status is { } statusFilter)
        {
            mapped = mapped.Where(m => m.FulfillmentStatus == statusFilter);
        }

        var ordered = mapped
            .OrderBy(m => m.FulfillmentStatus == SaleFulfillmentStatus.NeedsRescheduling ? 0 : 1)
            .ThenByDescending(m => m.SaleCreatedAtUtc)
            .ToList();

        var totals = new DeliveryFulfillmentReportTotalsDto(ordered.Count, ordered.Sum(m => m.DeliveryCharge));

        var pageSize = query.PageSize <= 0 ? PagedResult<DeliveryFulfillmentReportRowDto>.DefaultPageSize : query.PageSize;
        var page = Math.Max(query.Page, 1);
        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var totalPages = ordered.Count == 0 ? 0 : (int)Math.Ceiling(ordered.Count / (double)pageSize);

        return new DeliveryFulfillmentReportResultDto(
            new PagedResult<DeliveryFulfillmentReportRowDto>(pageItems, page, pageSize, ordered.Count, totalPages),
            totals);
    }
```

Add `using Negosio.Application.Delivery;` to `ReportsService.cs`'s usings (for `SaleFulfillmentCalculator`/`SaleFulfillmentStatus`).

- [ ] **Step 5: Wire the new report route**

In `ReportsController.cs`, add:

```csharp
    [HttpGet("delivery-fulfillment")]
    [ProducesResponseType(typeof(DeliveryFulfillmentReportResultDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<DeliveryFulfillmentReportResultDto>> DeliveryFulfillment(
        [FromQuery] DeliveryFulfillmentReportQuery query, CancellationToken cancellationToken)
        => Ok(await _reports.GetDeliveryFulfillmentAsync(query, cancellationToken));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Negosio.IntegrationTests --filter ReportsTests`
Expected: PASS — including every pre-existing `ReportsTests` scenario (KPIs, top products, categories are untouched by this task).

- [ ] **Step 7: Commit**

```bash
git add src/Negosio.Application/Reports/ReportsContracts.cs src/Negosio.Application/Reports/ReportsService.cs src/Negosio.Api/Controllers/ReportsController.cs tests/Negosio.IntegrationTests/Reports/ReportsTests.cs
git commit -m "fix(reports): aggregate delivery charges by distinct sale; add status/date filters and the Sale fulfillment view"
```

---

### Task 12: Concurrency integration tests — the races the Global Constraints call out by name

**Files:**
- Test: `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs` (new)

**Interfaces:**
- Consumes: `CreateAsync`/`CreateBatchAsync`'s `LockSaleItemsAsync` pessimistic lock (Task 9) and `MarkDeliveredAsync`/`CancelAsync`'s RowVersion handling (Task 10) — this task adds no production code, only proves those two mechanisms under real concurrent HTTP traffic (`Task.WhenAll`), following `VoidConcurrencyTests.cs`'s established template exactly (separate `HttpRequestMessage` + explicit per-request `Authorization` header, never the shared `Client.DefaultRequestHeaders`, which would race between two "actors").

- [ ] **Step 1: Write the tests**

Create `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptConcurrencyTests : IntegrationTest
{
    public DeliveryReceiptConcurrencyTests(NegosioApiFactory factory) : base(factory) { }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    // Same helper as VoidConcurrencyTests.cs — explicit per-request Authorization header so two
    // concurrent requests never race on Client.DefaultRequestHeaders.
    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scene(Guid SaleId, Guid SaleItemId, string Token);

    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal deliveryRequiredQuantity = 4m)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null, deliveryRequiredQuantity) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) }));

        return new Scene(sale.SaleId, sale.Items[0].SaleItemId, owner.AccessToken);
    }

    private static CreateDeliveryReceiptRequest FullyClaimingRequest(Scene s, decimal quantity) => new(
        Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
        new[] { new CreateDeliveryReceiptItemInput(s.SaleItemId, quantity) });

    [Fact]
    public async Task Two_concurrent_creates_claiming_the_same_last_units_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);

        var summary = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>(
            $"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);
        summary!.Items[0].PendingQuantity.Should().Be(4m); // never over-allocated past what was ever delivery-required
        summary.Items[0].AvailableToScheduleQuantity.Should().Be(0m);
    }

    [Fact]
    public async Task Rescheduling_after_a_cancel_races_correctly_against_a_second_claim_of_the_released_quantity()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var firstResponse = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var first = (await firstResponse.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel", new CancelDeliveryReceiptRequest("Wrong address"));

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
    }

    [Fact]
    public async Task Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        HttpRequestMessage DeliverRequest() => AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token);

        var results = await Task.WhenAll(Client.SendAsync(DeliverRequest()), Client.SendAsync(DeliverRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_mark_delivered_and_cancel_on_the_same_delivery_never_both_succeed()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        var deliverTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token));
        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/cancel", scene.Token, new CancelDeliveryReceiptRequest("Race")));

        var results = await Task.WhenAll(deliverTask, cancelTask);

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var final = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        final!.Status.Should().BeOneOf(DeliveryStatus.Delivered, DeliveryStatus.Cancelled);
    }
}
```

- [ ] **Step 2: Run the tests**

Run: `dotnet test tests/Negosio.IntegrationTests --filter DeliveryReceiptConcurrencyTests`
Expected: PASS. These tests are inherently timing-sensitive (real concurrent HTTP calls against a real LocalDB) — if any is flaky across a few reruns, that is a real signal the locking/RowVersion mechanism isn't working as designed, not a test to loosen; investigate `LockSaleItemsAsync`'s SQL and `DbUpdateConcurrencyException` handling before concluding it's "just flaky."

- [ ] **Step 3: Commit**

```bash
git add tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs
git commit -m "test(delivery): concurrent allocation and status-change races"
```

---

### Task 13: Backend full regression checkpoint

**Files:** none (verification-only task; fix forward in whichever file if something breaks).

**Interfaces:** none produced — this is the gate between the backend (Tasks 1-12) and the frontend (Tasks 14-19).

- [ ] **Step 1: Full backend test run**

Run: `dotnet test`
Expected: every test passes — every pre-existing suite (Catalog, Inventory, POS, Returns, Void, Staff, Branches, Reports, Receipt Settings, the prior Delivery-charge feature) plus every test this plan added (Tasks 1-3, 7, 9-12). Do not report this step's numbers without having actually run the command in this session — a prior plan in this same codebase once had an implementer report fabricated pass counts while the build was actually broken; verify by reading the real console output, not by assuming.

- [ ] **Step 2: Full build, both projects**

Run: `dotnet build Negosio.sln`
Expected: 0 errors, 0 new warnings beyond whatever the pre-existing baseline already had.

- [ ] **Step 3: If anything fails, fix forward**

Fix the specific failure in its owning file from Tasks 1-12 (do not patch symptoms in unrelated files). Re-run Steps 1-2 until both are green.

- [ ] **Step 4: Commit only if Step 3 required a fix**

```bash
git add -A
git commit -m "fix(delivery): backend regression fixes found at the full-suite checkpoint"
```

If Steps 1-2 were already green, there is nothing to commit — proceed straight to the frontend tasks.

---

## Frontend

### Task 14: Types + API client for delivery fulfillment

**Files:**
- Modify: `web/negosio-web/src/api/types.ts`
- Modify: `web/negosio-web/src/api/deliveryReceipts.ts` (full-file replacement)
- Modify: `web/negosio-web/src/api/reports.ts`

**Interfaces:**
- Produces: every TS type and API-client function Tasks 15-18 import. This is the frontend's coordination point, the same role Task 8 played on the backend — read it fully before starting any later frontend task. Field names mirror the backend DTOs byte-for-byte except camelCase (matches every existing type in this file).

No test of its own (no frontend test runner in this repo — see the plan's Tech Stack line). Verified by `npx tsc -b` at the end.

- [ ] **Step 1: Extend `CheckoutItemInput`/`SaleResultDto` in `types.ts`**

```typescript
export interface CheckoutItemInput {
  productVariantId: string
  quantity: number
  discount: CheckoutDiscountInput | null
  /** How much of this line is not taken at the counter today — defaults to 0 (all Take-now) when
   * omitted. Never negative, never more than `quantity`. */
  deliveryRequiredQuantity: number
}

export interface SaleResultItemDto {
  saleItemId: string
  productVariantId: string
  productName: string
  variantName: string | null
  quantity: number
  deliveryRequiredQuantity: number
}

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
  /** The SaleItems this checkout just created — needed to submit the follow-up delivery-schedule
   * batch for any line with deliveryRequiredQuantity > 0. */
  items: SaleResultItemDto[]
}
```

- [ ] **Step 2: Add `deliveryRequiredQuantity` to `SaleItemDto`**

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
}
```

(This mirrors the backend's `SaleItemDto` — `SaleQueryService` was not touched by the backend tasks, so confirm whether it already exposes this field; if Task 9-11 didn't add it there, treat this as a signal to add a one-line `deliveryRequiredQuantity: i.DeliveryRequiredQuantity` to the backend's `SaleItemDto`/`SaleQueryService.GetAsync` mapping now, as a small addition to this task rather than a separate backend task — it's a single field on an already-existing read path.)

- [ ] **Step 3: Replace the "Delivery receipt" section in full**

Replace everything from the `// ---- Delivery receipt ...` comment block through `CreateDeliveryReceiptRequest` with:

```typescript
// ---- Delivery fulfillment ----
// Backend contract (SalesController / DeliveryReceiptsController):
//   POST /api/sales/{saleId}/delivery-receipts/batch -> 201, DeliveryReceiptBatchResultDto
//   POST /api/sales/{saleId}/delivery-receipts        -> 201, DeliveryReceiptDto
//   GET  /api/sales/{saleId}/delivery-receipts        -> 200, DeliveryReceiptDto[]
//   GET  /api/sales/{saleId}/delivery-summary         -> 200, SaleDeliverySummaryDto
//   GET  /api/delivery-receipts/{id}                  -> 200 | 404
//   POST /api/delivery-receipts/{id}/deliver          -> 200, DeliveryReceiptDto
//   POST /api/delivery-receipts/{id}/cancel           -> 200, DeliveryReceiptDto (Owner/Admin/Manager only)
// A Sale may now have MULTIPLE DeliveryReceipts ("Delivery 1", "Delivery 2", ...); each shows only
// its own assigned items, never the whole sale.

export type DeliveryStatus = 'Pending' | 'Delivered' | 'Cancelled'

export interface DeliveryReceiptItemDto {
  saleItemId: string
  productName: string
  variantName: string | null
  quantity: number
  unitPrice: number | null
  amount: number | null
}

export interface DeliveryReceiptDto {
  id: string
  saleId: string | null
  relatedSaleNumber: string | null
  sequenceNumber: number
  scheduledDeliveryDate: string // yyyy-MM-dd
  status: DeliveryStatus
  createdAtUtc: string
  branchName: string
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  deliveryNotes: string | null
  preparedByName: string
  deliveredAtUtc: string | null
  deliveredByName: string | null
  cancelledAtUtc: string | null
  cancelledByName: string | null
  cancellationReason: string | null
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

export interface CreateDeliveryReceiptItemInput {
  saleItemId: string
  quantity: number
}

export interface CreateDeliveryReceiptRequest {
  scheduledDeliveryDate: string // yyyy-MM-dd
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  deliveryNotes: string | null
  items: CreateDeliveryReceiptItemInput[]
}

export interface CreateDeliveryReceiptBatchRequest {
  batchRequestId: string
  schedules: CreateDeliveryReceiptRequest[]
}

export interface DeliveryReceiptBatchResultDto {
  created: DeliveryReceiptDto[]
  wasExistingBatch: boolean
}

export interface CancelDeliveryReceiptRequest {
  reason: string
}

export type SaleFulfillmentStatus =
  | 'NotApplicable'
  | 'Unscheduled'
  | 'PartiallyScheduled'
  | 'FullyScheduled'
  | 'PartiallyDelivered'
  | 'FullyDelivered'
  | 'NeedsRescheduling'

export interface SaleItemFulfillmentDto {
  saleItemId: string
  productName: string
  variantName: string | null
  quantity: number
  takeNowQuantity: number
  deliveryRequiredQuantity: number
  pendingQuantity: number
  deliveredQuantity: number
  /** Never trust a frontend-computed version of this for a write — always send only what this field
   * currently says, and let the backend re-validate at submit time regardless. */
  availableToScheduleQuantity: number
}

export interface DeliveryReceiptSummaryDto {
  id: string
  sequenceNumber: number
  scheduledDeliveryDate: string
  status: DeliveryStatus
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  deliveryNotes: string | null
  deliveredAtUtc: string | null
  cancelledAtUtc: string | null
  cancellationReason: string | null
  items: DeliveryReceiptItemDto[]
}

export interface SaleDeliverySummaryDto {
  saleId: string
  fulfillmentStatus: SaleFulfillmentStatus
  deliveryCharge: number
  items: SaleItemFulfillmentDto[]
  deliveries: DeliveryReceiptSummaryDto[]
  canCreateDelivery: boolean
}
```

- [ ] **Step 4: Replace `deliveryReceipts.ts` in full**

```typescript
import { apiRequest } from './client'
import type {
  CancelDeliveryReceiptRequest,
  CreateDeliveryReceiptBatchRequest,
  CreateDeliveryReceiptRequest,
  DeliveryReceiptBatchResultDto,
  DeliveryReceiptDto,
  SaleDeliverySummaryDto,
} from './types'

/** Scheduled, partial, multi-delivery fulfillment. See the DTO block in types.ts for the full
 * backend-route contract. */
export const deliveryReceiptsApi = {
  createBatch: (saleId: string, body: CreateDeliveryReceiptBatchRequest) =>
    apiRequest<DeliveryReceiptBatchResultDto>(`/api/sales/${saleId}/delivery-receipts/batch`, {
      method: 'POST',
      body,
    }),

  create: (saleId: string, body: CreateDeliveryReceiptRequest) =>
    apiRequest<DeliveryReceiptDto>(`/api/sales/${saleId}/delivery-receipts`, { method: 'POST', body }),

  listForSale: (saleId: string) =>
    apiRequest<DeliveryReceiptDto[]>(`/api/sales/${saleId}/delivery-receipts`),

  getSaleSummary: (saleId: string) =>
    apiRequest<SaleDeliverySummaryDto>(`/api/sales/${saleId}/delivery-summary`),

  get: (id: string) => apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}`),

  markDelivered: (id: string) =>
    apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}/deliver`, { method: 'POST' }),

  cancel: (id: string, body: CancelDeliveryReceiptRequest) =>
    apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}/cancel`, { method: 'POST', body }),
}
```

Every existing caller of the old `getForSale`/`createForSale` shape (`PosTerminal.tsx`, `SaleDetailPage.tsx`, `CreateDeliveryReceiptModal.tsx`) is expected to fail to compile after this step — Tasks 15-16 fix those call sites. This mirrors the backend's own Task 8 → 9 pattern.

- [ ] **Step 5: Extend `reports.ts` / `types.ts` for the two report views**

In `types.ts`, replace the `DeliveryReportRowDto`/`DeliveryReportTotalsDto`/`DeliveryReportParams` block with:

```typescript
export type DeliveryReportPreset = 'All' | 'Today' | 'Upcoming' | 'Overdue' | 'Delivered' | 'Cancelled' | 'NeedsRescheduling'

export interface DeliveryReportRowDto {
  deliveryReceiptId: string
  saleId: string
  saleNumber: string
  sequenceNumber: number
  scheduledDeliveryDate: string
  status: DeliveryStatus
  isOverdue: boolean
  createdAtUtc: string
  recipientName: string
  deliveryAddress: string
  contactNumber: string | null
  deliveryNotes: string | null
  deliveryCharge: number
  saleGrandTotal: number
  paymentSummary: string
  preparedByName: string
  deliveredAtUtc: string | null
  cancelledAtUtc: string | null
  cancellationReason: string | null
}

export interface DeliveryReportTotalsDto {
  totalSchedules: number
  distinctSalesCount: number
  freeDeliverySalesCount: number
  chargedDeliverySalesCount: number
  totalDeliveryCharges: number
  averageDeliveryChargePerSale: number
}

export interface DeliveryReportResultDto {
  page: PagedResult<DeliveryReportRowDto>
  totals: DeliveryReportTotalsDto
}

export interface DeliveryReportParams {
  branchId?: string
  preset?: DeliveryReportPreset
  fromDate?: string
  toDate?: string
  status?: DeliveryStatus
  search?: string
  page?: number
  pageSize?: number
}

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

export interface DeliveryFulfillmentReportTotalsDto {
  totalSales: number
  totalDeliveryCharges: number
}

export interface DeliveryFulfillmentReportResultDto {
  page: PagedResult<DeliveryFulfillmentReportRowDto>
  totals: DeliveryFulfillmentReportTotalsDto
}

export interface DeliveryFulfillmentReportParams {
  branchId?: string
  status?: SaleFulfillmentStatus
  search?: string
  page?: number
  pageSize?: number
}
```

In `reports.ts`, read the file first to match its existing `apiRequest`/query-string-building convention (it already has a `deliveries` function using the old `DeliveryReportParams` shape — update its query-string building for the renamed/added params) and add:

```typescript
deliveryFulfillment: (params: DeliveryFulfillmentReportParams) =>
  apiRequest<DeliveryFulfillmentReportResultDto>(
    `/api/reports/delivery-fulfillment${toQueryString(params)}`,
  ),
```

(Use whatever the file's existing query-string helper is actually called — do not invent a new one; `toQueryString` here is illustrative, not literal.)

- [ ] **Step 6: Confirm the expected compile failures, then commit**

Run: `npx tsc -b` (from `web/negosio-web`)
Expected: errors in `PosTerminal.tsx`, `DeliveryDetailsFields.tsx`, `PaymentSuccessModal.tsx`, `SaleDetailPage.tsx`, `CreateDeliveryReceiptModal.tsx`, `DeliveryReceiptPage.tsx`, `DeliveryReportsPage.tsx` — every one of them is fixed in Tasks 15-18. Confirm there are no errors anywhere else (a stray error outside this list means a type was renamed in a way that broke something unexpected — fix the type, not the unrelated file).

```bash
git add web/negosio-web/src/api/types.ts web/negosio-web/src/api/deliveryReceipts.ts web/negosio-web/src/api/reports.ts
git commit -m "feat(web): types and API client for scheduled, partial, multi-delivery fulfillment"
```

---

### Task 15: POS — per-item delivery allocation, multiple schedules, post-checkout batch submission

**Files:**
- Modify: `web/negosio-web/src/lib/pos.ts`
- Modify: `web/negosio-web/src/components/pos/DeliveryDetailsFields.tsx` (full-file replacement)
- Modify: `web/negosio-web/src/components/pos/PaymentModal.tsx`
- Modify: `web/negosio-web/src/components/pos/PaymentSuccessModal.tsx`
- Modify: `web/negosio-web/src/components/pos/PosTerminal.tsx`

**Interfaces:**
- Consumes: `CheckoutItemInput.deliveryRequiredQuantity`, `SaleResultDto.items` (Task 14), `deliveryReceiptsApi.createBatch` (Task 14).
- Produces: the POS checkout flow that satisfies the spec's per-item allocation UI, "+ Add another delivery schedule", and the 11-step checkout workflow (validate → checkout with per-item `DeliveryRequiredQuantity` → batch-schedule the result). No backend interface changes.

No automated test (no frontend runner in this repo). Verified by `npx tsc -b` plus a manual dev-server walkthrough at the end of this task.

- [ ] **Step 1: Extend `lib/pos.ts`**

Remove `DeliveryFields`/`EMPTY_DELIVERY_FIELDS` (superseded) and add:

```typescript
export interface DeliveryScheduleItemAllocation {
  variantId: string
  quantity: number
}

/** One delivery schedule being built in the payment modal. `key` is a local React/list-diffing id
 * only — never sent to the backend. Maps to `CreateDeliveryReceiptRequest` once the sale exists and
 * `variantId`s can be resolved to real `saleItemId`s (see PosTerminal's batch-submit mutation). */
export interface DeliverySchedule {
  key: string
  scheduledDeliveryDate: string // yyyy-MM-dd, browser-local — see the plan's Global Constraints
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  deliveryNotes: string
  items: DeliveryScheduleItemAllocation[]
}

/** Browser-local "today" as a `yyyy-MM-dd` date-input value. This repo has no tenant-timezone model
 * on the frontend yet (see the plan's Global Constraints) — this is a UI default only; the backend
 * independently rejects a past date against its own business-local clock regardless of what this
 * produces. */
export function todayLocalDateInput(): string {
  const d = new Date()
  const yyyy = d.getFullYear()
  const mm = String(d.getMonth() + 1).padStart(2, '0')
  const dd = String(d.getDate()).padStart(2, '0')
  return `${yyyy}-${mm}-${dd}`
}

export function emptyDeliverySchedule(): DeliverySchedule {
  return {
    key: crypto.randomUUID(),
    scheduledDeliveryDate: todayLocalDateInput(),
    recipientName: '',
    deliveryAddress: '',
    contactNumber: '',
    deliveryNotes: '',
    items: [],
  }
}

/** Sum of quantity a variant already has allocated across every schedule except `excludeKey` (pass
 * the schedule currently being edited so its own not-yet-committed value doesn't count against
 * itself). Used only to compute a helpful UI cap — the backend re-validates independently and is
 * the only source of truth for what's actually available (see the plan's Global Constraints). */
export function scheduledQuantityFor(
  schedules: DeliverySchedule[],
  variantId: string,
  excludeKey?: string,
): number {
  return schedules
    .filter((s) => s.key !== excludeKey)
    .reduce((sum, s) => sum + (s.items.find((i) => i.variantId === variantId)?.quantity ?? 0), 0)
}
```

Keep `EMPTY_DELIVERY_CHARGE`, `isValidDeliveryChargeInput`, `parseDeliveryCharge`, `suggestCashButtons`, `LastSaleRef`, everything else in the file unchanged.

- [ ] **Step 2: Replace `DeliveryDetailsFields.tsx` in full**

```tsx
import type { DeliverySchedule } from '../../lib/pos'
import { scheduledQuantityFor, todayLocalDateInput } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { Button, TextArea, TextField } from '../ui'

interface CartLineInfo {
  variantId: string
  name: string
  variantName: string | null
  quantity: number
}

interface Props {
  cartLines: CartLineInfo[]
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
  /** variantId -> quantity of that line marked for delivery (0 = entirely Take-now). Every line not
   * present here is treated as 0. */
  deliveryRequiredByVariant: Record<string, number>
  onDeliveryRequiredChange: (variantId: string, quantity: number) => void
  schedules: DeliverySchedule[]
  onScheduleFieldChange: (key: string, patch: Partial<Omit<DeliverySchedule, 'key' | 'items'>>) => void
  onScheduleItemChange: (key: string, variantId: string, quantity: number) => void
  onAddSchedule: () => void
  onRemoveSchedule: (key: string) => void
  errors: { deliveryCharge?: string }
  /** True once the cashier has attempted to confirm payment with an incomplete schedule — gates
   * showing per-schedule "required" errors, matching PaymentModal's existing `deliveryAttempted`
   * convention for the charge/recipient fields. */
  attempted: boolean
  disabled?: boolean
}

export function DeliveryDetailsFields({
  cartLines,
  deliveryCharge,
  onDeliveryChargeChange,
  deliveryRequiredByVariant,
  onDeliveryRequiredChange,
  schedules,
  onScheduleFieldChange,
  onScheduleItemChange,
  onAddSchedule,
  onRemoveSchedule,
  errors,
  attempted,
  disabled,
}: Props) {
  const isFree = errors.deliveryCharge == null && Number(deliveryCharge) === 0
  const deliveryItemLines = cartLines.filter((l) => (deliveryRequiredByVariant[l.variantId] ?? 0) > 0)

  return (
    <div className="mt-3 space-y-4 border-t border-border pt-3">
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

      <div className="space-y-2">
        <p className="text-sm font-semibold text-text-secondary">Items for delivery</p>
        {cartLines.map((l) => {
          const required = deliveryRequiredByVariant[l.variantId] ?? 0
          return (
            <div key={l.variantId} className="flex items-center justify-between gap-3 rounded-lg border border-border bg-surface-subtle px-3 py-2">
              <div className="min-w-0">
                <p className="truncate text-sm font-medium text-text-primary">
                  {l.name}
                  {l.variantName && <span className="text-text-muted"> · {l.variantName}</span>}
                </p>
                <p className="text-[12px] text-text-muted">
                  Sold {formatQty(l.quantity)} · Take-now {formatQty(l.quantity - required)}
                </p>
              </div>
              <label className="flex shrink-0 items-center gap-2 text-[13px] text-text-secondary">
                For delivery
                <input
                  type="number"
                  min={0}
                  max={l.quantity}
                  step="0.001"
                  value={required}
                  disabled={disabled}
                  onChange={(e) => {
                    const v = Math.max(0, Math.min(Number(e.target.value) || 0, l.quantity))
                    onDeliveryRequiredChange(l.variantId, v)
                  }}
                  aria-label={`Delivery quantity for ${l.name}`}
                  className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                />
              </label>
            </div>
          )
        })}
      </div>

      {deliveryItemLines.length > 0 && (
        <div className="space-y-3">
          <p className="text-sm font-semibold text-text-secondary">Delivery schedules</p>
          {schedules.map((s, idx) => {
            const showErrors = attempted && s.items.some((i) => i.quantity > 0)
            return (
              <div key={s.key} className="space-y-3 rounded-xl border border-border-strong p-3">
                <div className="flex items-center justify-between">
                  <p className="text-[13px] font-semibold text-text-primary">Delivery {idx + 1}</p>
                  {schedules.length > 1 && (
                    <button
                      type="button"
                      onClick={() => onRemoveSchedule(s.key)}
                      disabled={disabled}
                      className="text-[12px] font-semibold text-danger hover:underline"
                    >
                      Remove
                    </button>
                  )}
                </div>

                <TextField
                  label="Scheduled delivery date *"
                  name={`scheduledDeliveryDate-${s.key}`}
                  type="date"
                  min={todayLocalDateInput()}
                  value={s.scheduledDeliveryDate}
                  onChange={(e) => onScheduleFieldChange(s.key, { scheduledDeliveryDate: e.target.value })}
                  error={showErrors && !s.scheduledDeliveryDate ? 'A delivery date is required.' : undefined}
                  disabled={disabled}
                />
                <TextField
                  label="Recipient name *"
                  name={`recipientName-${s.key}`}
                  value={s.recipientName}
                  onChange={(e) => onScheduleFieldChange(s.key, { recipientName: e.target.value })}
                  error={showErrors && !s.recipientName.trim() ? 'Recipient name is required.' : undefined}
                  disabled={disabled}
                />
                <TextArea
                  label="Recipient address *"
                  name={`deliveryAddress-${s.key}`}
                  rows={2}
                  value={s.deliveryAddress}
                  onChange={(e) => onScheduleFieldChange(s.key, { deliveryAddress: e.target.value })}
                  error={showErrors && !s.deliveryAddress.trim() ? 'Recipient address is required.' : undefined}
                  disabled={disabled}
                />
                <div className="grid gap-x-3 sm:grid-cols-2">
                  <TextField
                    label="Contact number"
                    name={`contactNumber-${s.key}`}
                    value={s.contactNumber}
                    onChange={(e) => onScheduleFieldChange(s.key, { contactNumber: e.target.value })}
                    disabled={disabled}
                  />
                  <TextField
                    label="Delivery notes (optional)"
                    name={`deliveryNotes-${s.key}`}
                    value={s.deliveryNotes}
                    onChange={(e) => onScheduleFieldChange(s.key, { deliveryNotes: e.target.value })}
                    disabled={disabled}
                  />
                </div>

                <div className="space-y-1.5">
                  {deliveryItemLines.map((l) => {
                    const required = deliveryRequiredByVariant[l.variantId] ?? 0
                    const claimedElsewhere = scheduledQuantityFor(schedules, l.variantId, s.key)
                    const cap = Math.max(0, required - claimedElsewhere)
                    const value = s.items.find((i) => i.variantId === l.variantId)?.quantity ?? 0
                    return (
                      <label
                        key={l.variantId}
                        className="flex items-center justify-between gap-3 text-[13px] text-text-secondary"
                      >
                        <span className="truncate">
                          {l.name}
                          {l.variantName ? ` · ${l.variantName}` : ''}
                        </span>
                        <input
                          type="number"
                          min={0}
                          max={Math.max(cap, value)}
                          step="0.001"
                          value={value}
                          disabled={disabled}
                          onChange={(e) => {
                            const v = Math.max(0, Math.min(Number(e.target.value) || 0, cap))
                            onScheduleItemChange(s.key, l.variantId, v)
                          }}
                          aria-label={`Quantity of ${l.name} on Delivery ${idx + 1}`}
                          className="h-8 w-16 shrink-0 rounded-md border border-border-strong bg-white px-2 text-right text-[13px] focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                        />
                      </label>
                    )
                  })}
                </div>
              </div>
            )
          })}
          <Button type="button" variant="secondary" size="sm" onClick={onAddSchedule} disabled={disabled}>
            + Add another delivery schedule
          </Button>
        </div>
      )}
    </div>
  )
}
```

`max={Math.max(cap, value)}` is a defensive clamp so a stale `value` above a just-lowered `cap` (from another schedule claiming more of the same item) never renders an `<input max>` below its own `value` — React/HTML would otherwise silently let the field show an out-of-range number; the `onChange` handler still clamps every new edit to `cap`, so this only affects how the currently-stale value is displayed, never what a fresh edit can save. Confirm `TextArea`/`TextField`/`Button` accept the props used here (`error`, `type="date"`, `type="button"`, `min`) by checking `web/negosio-web/src/components/ui/` first — match their real prop names exactly if they differ.

- [ ] **Step 3: Update `PaymentModal.tsx`'s props and validation**

Replace the `forDelivery`/`deliveryFields`/`onDeliveryFieldsChange` props with the new shape and rewire `deliveryComplete`/`deliveryErrors`:

```tsx
interface Props {
  open: boolean
  onClose: () => void
  amountDue: number
  submitting: boolean
  error: string | null
  onConfirm: (payment: CheckoutPaymentInput) => void
  forDelivery: boolean
  onToggleForDelivery: (next: boolean) => void
  cartLines: { variantId: string; name: string; variantName: string | null; quantity: number }[]
  deliveryRequiredByVariant: Record<string, number>
  onDeliveryRequiredChange: (variantId: string, quantity: number) => void
  schedules: DeliverySchedule[]
  onScheduleFieldChange: (key: string, patch: Partial<Omit<DeliverySchedule, 'key' | 'items'>>) => void
  onScheduleItemChange: (key: string, variantId: string, quantity: number) => void
  onAddSchedule: () => void
  onRemoveSchedule: (key: string) => void
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
}
```

Inside the component, replace the `deliveryComplete`/`deliveryErrors` block:

```tsx
  const activeSchedules = schedules.filter((s) => s.items.some((i) => i.quantity > 0))
  const deliveryComplete =
    !forDelivery ||
    (deliveryChargeValid &&
      activeSchedules.every((s) => s.scheduledDeliveryDate && s.recipientName.trim() && s.deliveryAddress.trim()))
  const deliveryErrors = forDelivery && deliveryAttempted ? { deliveryCharge: deliveryChargeValid ? undefined : 'Enter a valid amount (0 or more, up to 2 decimal places).' } : {}
```

(`activeSchedules` — a schedule with no items allocated is simply not submitted; only a schedule the cashier actually put quantities on needs its recipient/address/date filled in.)

Update the JSX at the bottom to pass the new props through to `DeliveryDetailsFields` instead of the old `values`/`onChange`.

- [ ] **Step 4: Wire `PaymentSuccessModal.tsx` for zero-or-more created schedules**

Replace the single `deliveryReceiptId: string | null` prop with `deliveryCount: number` (just enough to render the confirmation banner — printing now happens from the Sale-detail page, not straight out of this modal, since there is no longer exactly one document to print):

```tsx
interface Props {
  open: boolean
  onClose: () => void
  result: SaleResultDto | null
  payment: CheckoutPaymentInput | null
  /** How many delivery schedules were created for this sale (0 for a normal or charge-only sale). */
  deliveryCount: number
  onNewTransaction: () => void
}
```

Replace every `deliveryReceiptId &&` conditional with `deliveryCount > 0 &&`; replace the "Print delivery receipt" button (which pointed at one specific DR id) with a link to the sale itself, since there may now be several documents:

```tsx
        {deliveryCount > 0 && (
          <a
            href={`/sales/${result.saleId}`}
            className="flex h-12 w-full items-center justify-center gap-2 rounded-lg border border-primary-200 bg-white text-sm font-semibold text-primary-700 transition-colors hover:bg-primary-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
          >
            <Truck className="size-4" aria-hidden="true" />
            View {deliveryCount === 1 ? 'delivery schedule' : `${deliveryCount} delivery schedules`}
          </a>
        )}
```

- [ ] **Step 5: Wire it all through `PosTerminal.tsx`**

Replace the `forDelivery`/`deliveryFields`/`deliveryCharge`/`successDeliveryReceiptId`/`createDrMutation` block with:

```tsx
  const [forDelivery, setForDelivery] = useState(false)
  const [deliveryCharge, setDeliveryCharge] = useState(EMPTY_DELIVERY_CHARGE)
  const [deliveryRequiredByVariant, setDeliveryRequiredByVariant] = useState<Record<string, number>>({})
  const [schedules, setSchedules] = useState<DeliverySchedule[]>([emptyDeliverySchedule()])
  const [successDeliveryCount, setSuccessDeliveryCount] = useState(0)
  const batchIdRef = useRef<string | null>(null)

  const ensureBatchId = useCallback(() => {
    if (!batchIdRef.current) batchIdRef.current = crypto.randomUUID()
    return batchIdRef.current
  }, [])

  const resetDeliveryState = useCallback(() => {
    setForDelivery(false)
    setDeliveryCharge(EMPTY_DELIVERY_CHARGE)
    setDeliveryRequiredByVariant({})
    setSchedules([emptyDeliverySchedule()])
    batchIdRef.current = null
  }, [])

  const onToggleForDelivery = useCallback((next: boolean) => {
    setForDelivery(next)
    if (!next) {
      setDeliveryCharge(EMPTY_DELIVERY_CHARGE)
      setDeliveryRequiredByVariant({})
      setSchedules([emptyDeliverySchedule()])
      batchIdRef.current = null
    }
  }, [])

  const onDeliveryChargeChange = useCallback((value: string) => setDeliveryCharge(value), [])

  // Setting an item's delivery-required quantity auto-fills the FIRST schedule with the newly
  // available amount (minus whatever other schedules already claim of the same item) — the common
  // case (one schedule for everything marked for delivery) needs zero extra clicks; a cashier adding
  // more schedules can then rebalance quantities across them freely.
  const onDeliveryRequiredChange = useCallback((variantId: string, quantity: number) => {
    setDeliveryRequiredByVariant((prev) => ({ ...prev, [variantId]: quantity }))
    setSchedules((prev) => {
      if (prev.length === 0) return prev
      const [first, ...rest] = prev
      const claimedByOthers = scheduledQuantityFor(rest, variantId)
      const target = Math.max(0, Math.min(quantity - claimedByOthers, quantity))
      const exists = first.items.some((i) => i.variantId === variantId)
      const items = exists
        ? first.items.map((i) => (i.variantId === variantId ? { ...i, quantity: target } : i))
        : target > 0
          ? [...first.items, { variantId, quantity: target }]
          : first.items
      return [{ ...first, items }, ...rest]
    })
  }, [])

  const onScheduleFieldChange = useCallback(
    (key: string, patch: Partial<Omit<DeliverySchedule, 'key' | 'items'>>) =>
      setSchedules((prev) => prev.map((s) => (s.key === key ? { ...s, ...patch } : s))),
    [],
  )
  const onScheduleItemChange = useCallback((key: string, variantId: string, quantity: number) => {
    setSchedules((prev) =>
      prev.map((s) => {
        if (s.key !== key) return s
        const exists = s.items.some((i) => i.variantId === variantId)
        const items = exists
          ? s.items.map((i) => (i.variantId === variantId ? { ...i, quantity } : i))
          : [...s.items, { variantId, quantity }]
        return { ...s, items: items.filter((i) => i.quantity > 0) }
      }),
    )
  }, [])
  const onAddSchedule = useCallback(() => setSchedules((prev) => [...prev, emptyDeliverySchedule()]), [])
  const onRemoveSchedule = useCallback(
    (key: string) => setSchedules((prev) => (prev.length > 1 ? prev.filter((s) => s.key !== key) : prev)),
    [],
  )

  // Schedules the sale's delivery-required quantities right after checkout succeeds. A failure here
  // never unwinds the sale — it stays Completed — it only tells the cashier to finish scheduling from
  // the Sale page. Idempotent server-side via batchIdRef's BatchRequestId: retrying (e.g. the cashier
  // clicking retry after seeing the toast) resubmits the SAME batch id and returns the original rows.
  const createDeliveryBatchMutation = useMutation({
    mutationFn: ({ saleId, resultItems }: { saleId: string; resultItems: SaleResultDto['items'] }) => {
      const activeSchedules = schedules.filter((s) => s.items.some((i) => i.quantity > 0))
      return deliveryReceiptsApi.createBatch(saleId, {
        batchRequestId: ensureBatchId(),
        schedules: activeSchedules.map((s) => ({
          scheduledDeliveryDate: s.scheduledDeliveryDate,
          recipientName: s.recipientName.trim(),
          deliveryAddress: s.deliveryAddress.trim(),
          contactNumber: s.contactNumber.trim() || null,
          deliveryNotes: s.deliveryNotes.trim() || null,
          items: s.items
            .filter((i) => i.quantity > 0)
            .map((i) => ({
              saleItemId: resultItems.find((ri) => ri.productVariantId === i.variantId)!.saleItemId,
              quantity: i.quantity,
            })),
        })),
      })
    },
    onSuccess: (result) => setSuccessDeliveryCount(result.created.length),
    onError: () => {
      toast(
        'error',
        'Sale completed, but the delivery schedule could not be created. Schedule it from the sale page.',
      )
    },
  })
```

Remove the old `createDrMutation` entirely and its two call sites (`PaymentSuccessModal`'s `deliveryReceiptId` prop, the `window.open` inside `createDrMutation.onSuccess`).

In the checkout `mutation`'s `mutationFn`, add per-item `deliveryRequiredQuantity` to the built body:

```tsx
        items: cart.lines.map((l) => ({
          productVariantId: l.variantId,
          quantity: l.quantity,
          discount: l.discount.type === 'None' ? null : l.discount,
          deliveryRequiredQuantity: deliveryRequiredByVariant[l.variantId] ?? 0,
        })),
```

In the checkout mutation's `onSuccess`, replace the delivery-receipt-creation block:

```tsx
      const activeSchedules = schedules.filter((s) => s.items.some((i) => i.quantity > 0))
      if (forDelivery && activeSchedules.length > 0) {
        createDeliveryBatchMutation.mutate({ saleId: result.saleId, resultItems: result.items })
      } else {
        setSuccessDeliveryCount(0)
      }
      resetDeliveryState()
```

(`resetDeliveryState` replaces the old three separate `setForDelivery(false)` / `setDeliveryFields(...)` / `setDeliveryCharge(...)` lines — call it AFTER reading `schedules`/`forDelivery` for the batch-submit above, never before.)

In `confirmNewTransaction`, replace the three old delivery-reset lines and `setSuccessDeliveryReceiptId(null)` with `resetDeliveryState()` and `setSuccessDeliveryCount(0)`.

Update the `<PaymentModal>` JSX to pass the new props (`cartLines={cart.lines}`, `deliveryRequiredByVariant`, `onDeliveryRequiredChange`, `schedules`, `onScheduleFieldChange`, `onScheduleItemChange`, `onAddSchedule`, `onRemoveSchedule`) instead of `deliveryFields`/`onDeliveryFieldsChange`.

Update `<PaymentSuccessModal>`'s `deliveryReceiptId={successDeliveryReceiptId}` to `deliveryCount={successDeliveryCount}`, and its `onClose`/`onNewTransaction` callbacks to `setSuccessDeliveryCount(0)` instead of `setSuccessDeliveryReceiptId(null)`.

Add the new imports: `import { deliveryReceiptsApi } from '../../api/deliveryReceipts'` (already imported), `import { emptyDeliverySchedule, scheduledQuantityFor, type DeliverySchedule } from '../../lib/pos'` alongside the existing `lib/pos` import, remove `EMPTY_DELIVERY_FIELDS`/`DeliveryFields` from that same import line (superseded).

- [ ] **Step 6: Type-check, then manually verify in the dev server**

Run: `npx tsc -b` (from `web/negosio-web`)
Expected: no errors in any of the five files this task touched. Some other files (`SaleDetailPage.tsx`, `CreateDeliveryReceiptModal.tsx`, `DeliveryReceiptPage.tsx`, `DeliveryReportsPage.tsx`) are still expected to fail — Tasks 16-18 fix them.

Then start both dev servers (backend `dotnet run --project src/Negosio.Api`, frontend `npm run dev` inside `web/negosio-web`) and manually walk through, in a browser:
1. Add two different products to the cart, open Payment, tick "For delivery", mark one item's full quantity and the other's partial quantity for delivery, leave one schedule, fill recipient/address/date, confirm payment with cash. Verify the success modal shows "View 1 delivery schedule" and the sale completes.
2. Repeat, but click "+ Add another delivery schedule" and split one item's delivery quantity across two schedules with different dates — verify both schedules get created (check the Sale-detail page — Task 16 isn't built yet, so verify via a direct `GET /api/sales/{id}/delivery-receipts` call, e.g. via the browser's dev tools network tab or a REST client) and neither over-allocates.
3. Repeat with "For delivery" left ticked but a delivery charge set and NO item marked for delivery — verify checkout still succeeds with the charge applied and no batch call is made (this is the pre-existing delivery-charge-only flow — it must be completely unaffected).

Report the outcome of this manual walkthrough in this task's completion notes — do not claim it "should work" without having actually driven it in a browser.

- [ ] **Step 7: Commit**

```bash
git add web/negosio-web/src/lib/pos.ts web/negosio-web/src/components/pos/DeliveryDetailsFields.tsx web/negosio-web/src/components/pos/PaymentModal.tsx web/negosio-web/src/components/pos/PaymentSuccessModal.tsx web/negosio-web/src/components/pos/PosTerminal.tsx
git commit -m "feat(web): POS per-item delivery allocation, multiple schedules, batch submission"
```

---

### Task 16: Sale-detail page — fulfillment breakdown, delivery list, create/deliver/cancel/reschedule

**Files:**
- Modify: `web/negosio-web/src/lib/useCan.ts`
- Modify: `web/negosio-web/src/lib/pos.ts`
- Modify: `web/negosio-web/src/components/sales/CreateDeliveryReceiptModal.tsx` (full-file replacement — becomes the "Create delivery" / "Schedule again" modal)
- Create: `web/negosio-web/src/components/sales/CancelDeliveryModal.tsx`
- Create: `web/negosio-web/src/components/sales/DeliveryStatusBadge.tsx`
- Modify: `web/negosio-web/src/pages/SaleDetailPage.tsx`

**Interfaces:**
- Consumes: `deliveryReceiptsApi.{getSaleSummary,create,markDelivered,cancel}` (Task 14).
- Produces: the Sale-detail fulfillment UI every one of the spec's per-sale actions (create, mark delivered, cancel, reschedule) lives on.

No automated test. Verified by `npx tsc -b` plus a manual walkthrough.

- [ ] **Step 1: Add the `delivery:cancel` capability**

In `useCan.ts`, add `'delivery:cancel'` to the `Capability` union and:

```typescript
  // Mirrors AuthorizationPolicies.DeliveryCancel — Owner/Admin/Manager only.
  'delivery:cancel': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
```

- [ ] **Step 2: Add status labels/tones to `lib/pos.ts`**

```typescript
export const DELIVERY_STATUS_LABELS: Record<DeliveryStatus, string> = {
  Pending: 'Pending',
  Delivered: 'Delivered',
  Cancelled: 'Cancelled',
}

export function deliveryStatusTone(s: DeliveryStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (s) {
    case 'Pending':
      return 'warning'
    case 'Delivered':
      return 'success'
    case 'Cancelled':
      return 'neutral'
  }
}

export const SALE_FULFILLMENT_STATUS_LABELS: Record<SaleFulfillmentStatus, string> = {
  NotApplicable: 'Not applicable',
  Unscheduled: 'Unscheduled',
  PartiallyScheduled: 'Partially scheduled',
  FullyScheduled: 'Fully scheduled',
  PartiallyDelivered: 'Partially delivered',
  FullyDelivered: 'Fully delivered',
  NeedsRescheduling: 'Needs rescheduling',
}

export function saleFulfillmentStatusTone(s: SaleFulfillmentStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (s) {
    case 'FullyDelivered':
      return 'success'
    case 'PartiallyDelivered':
    case 'FullyScheduled':
    case 'PartiallyScheduled':
      return 'warning'
    case 'NeedsRescheduling':
      return 'danger'
    case 'Unscheduled':
    case 'NotApplicable':
      return 'neutral'
  }
}
```

Add `import type { DeliveryStatus, SaleFulfillmentStatus } from '../api/types'` to this file's existing type-only import line.

- [ ] **Step 3: Create `DeliveryStatusBadge.tsx`**

```tsx
import type { DeliveryStatus } from '../../api/types'
import { DELIVERY_STATUS_LABELS, deliveryStatusTone } from '../../lib/pos'
import { Badge } from '../ui'

export function DeliveryStatusBadge({ status }: { status: DeliveryStatus }) {
  return <Badge tone={deliveryStatusTone(status)}>{DELIVERY_STATUS_LABELS[status]}</Badge>
}
```

- [ ] **Step 4: Replace `CreateDeliveryReceiptModal.tsx` in full**

Renamed in spirit to "create or reschedule a delivery" — same filename and export name kept so `SaleDetailPage.tsx`'s import doesn't need to change, only its usage.

```tsx
import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { deliveryReceiptsApi } from '../../api/deliveryReceipts'
import type { CreateDeliveryReceiptRequest, SaleItemFulfillmentDto } from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { todayLocalDateInput } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { Button, Modal, TextArea, TextField, useToast } from '../ui'

/** Optional prefill for "Schedule again" after a cancellation — recipient/address/contact/notes and
 * the item quantities the cancelled delivery carried. Quantities are re-clamped against each item's
 * CURRENT `availableToScheduleQuantity` at render time (never trusted as still-valid), since another
 * user may have consumed some of the released quantity in the meantime. */
export interface DeliveryPrefill {
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  deliveryNotes: string
  itemQuantities: Record<string, number> // saleItemId -> quantity
}

interface Props {
  open: boolean
  onClose: () => void
  saleId: string
  /** Every sale item with availableToScheduleQuantity > 0 right now — items fully scheduled/delivered
   * simply don't appear here, so there is nothing to accidentally over-claim. */
  availableItems: SaleItemFulfillmentDto[]
  prefill?: DeliveryPrefill
}

interface FieldErrors {
  scheduledDeliveryDate?: string
  recipientName?: string
  deliveryAddress?: string
}

export function CreateDeliveryReceiptModal({ open, onClose, saleId, availableItems, prefill }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [scheduledDeliveryDate, setScheduledDeliveryDate] = useState(todayLocalDateInput())
  const [recipientName, setRecipientName] = useState('')
  const [deliveryAddress, setDeliveryAddress] = useState('')
  const [contactNumber, setContactNumber] = useState('')
  const [deliveryNotes, setDeliveryNotes] = useState('')
  const [qty, setQty] = useState<Record<string, string>>({})
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setScheduledDeliveryDate(todayLocalDateInput())
    setRecipientName(prefill?.recipientName ?? '')
    setDeliveryAddress(prefill?.deliveryAddress ?? '')
    setContactNumber(prefill?.contactNumber ?? '')
    setDeliveryNotes(prefill?.deliveryNotes ?? '')
    // Clamp any prefilled quantity to what's actually available right now.
    const initialQty: Record<string, string> = {}
    for (const item of availableItems) {
      const wanted = prefill?.itemQuantities[item.saleItemId] ?? item.availableToScheduleQuantity
      initialQty[item.saleItemId] = String(Math.min(wanted, item.availableToScheduleQuantity))
    }
    setQty(initialQty)
    setFieldErrors({})
  }, [open, prefill, availableItems])

  const mutation = useMutation({
    mutationFn: () => {
      const body: CreateDeliveryReceiptRequest = {
        scheduledDeliveryDate,
        recipientName: recipientName.trim(),
        deliveryAddress: deliveryAddress.trim(),
        contactNumber: contactNumber.trim() || null,
        deliveryNotes: deliveryNotes.trim() || null,
        items: availableItems
          .map((i) => ({ saleItemId: i.saleItemId, quantity: Number(qty[i.saleItemId] ?? '0') }))
          .filter((l) => l.quantity > 0),
      }
      return deliveryReceiptsApi.create(saleId, body)
    },
    onSuccess: (dr) => {
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-summary'] })
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-receipts'] })
      onClose()
      window.open('/delivery-receipts/' + dr.id + '?print=1', '_blank', 'noopener')
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      const next: FieldErrors = {}
      if (fields.recipientname) next.recipientName = fields.recipientname
      if (fields.deliveryaddress) next.deliveryAddress = fields.deliveryaddress
      if (fields.scheduleddeliverydate) next.scheduledDeliveryDate = fields.scheduleddeliverydate
      if (Object.keys(next).length > 0) {
        setFieldErrors(next)
        return
      }
      if (err instanceof ApiError && err.code === 'DELIVERY_QUANTITY_EXCEEDS_AVAILABLE') {
        toast('error', `${err.message} Refresh to see the current availability.`)
        qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-summary'] })
        return
      }
      toast(
        'error',
        err instanceof ApiError || err instanceof Error ? err.message : 'Could not create the delivery.',
      )
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setFieldErrors({})

    const next: FieldErrors = {}
    if (!scheduledDeliveryDate) next.scheduledDeliveryDate = 'A delivery date is required.'
    if (!recipientName.trim()) next.recipientName = 'Recipient name is required.'
    if (!deliveryAddress.trim()) next.deliveryAddress = 'Recipient address is required.'
    if (Object.keys(next).length > 0) {
      setFieldErrors(next)
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={prefill ? 'Schedule delivery again' : 'Create delivery'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            Create &amp; print
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="max-h-[62vh] space-y-4 overflow-y-auto pr-1">
        <TextField
          label="Scheduled delivery date"
          name="scheduledDeliveryDate"
          type="date"
          min={todayLocalDateInput()}
          value={scheduledDeliveryDate}
          onChange={(e) => setScheduledDeliveryDate(e.target.value)}
          error={fieldErrors.scheduledDeliveryDate || undefined}
          autoFocus
        />
        <TextField
          label="Recipient name"
          name="recipientName"
          value={recipientName}
          onChange={(e) => setRecipientName(e.target.value)}
          error={fieldErrors.recipientName || undefined}
        />
        <TextArea
          label="Recipient address"
          name="deliveryAddress"
          rows={2}
          value={deliveryAddress}
          onChange={(e) => setDeliveryAddress(e.target.value)}
          error={fieldErrors.deliveryAddress || undefined}
        />
        <TextField
          label="Contact number"
          name="contactNumber"
          value={contactNumber}
          onChange={(e) => setContactNumber(e.target.value)}
        />
        <TextArea
          label="Delivery notes"
          name="deliveryNotes"
          rows={2}
          value={deliveryNotes}
          onChange={(e) => setDeliveryNotes(e.target.value)}
        />

        <div className="space-y-2">
          <p className="text-sm font-semibold text-text-secondary">Items on this delivery</p>
          {availableItems.map((i) => (
            <div key={i.saleItemId} className="rounded-lg border border-border bg-surface-subtle p-3">
              <p className="text-sm font-medium text-text-primary">
                {i.productName}
                {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
              </p>
              <p className="mt-0.5 text-[12px] text-text-muted">
                Available to schedule: {formatQty(i.availableToScheduleQuantity)}
              </p>
              <label className="mt-2 flex items-center gap-2 text-[13px] text-text-secondary">
                Quantity
                <input
                  type="number"
                  min={0}
                  max={i.availableToScheduleQuantity}
                  step="0.001"
                  value={qty[i.saleItemId] ?? '0'}
                  onChange={(e) => setQty((p) => ({ ...p, [i.saleItemId]: e.target.value }))}
                  aria-label={`Delivery quantity for ${i.productName}`}
                  className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                />
              </label>
            </div>
          ))}
        </div>
      </form>
    </Modal>
  )
}
```

- [ ] **Step 5: Create `CancelDeliveryModal.tsx`**

Mirrors `VoidSaleModal.tsx`'s reason-required confirm template exactly.

```tsx
import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { deliveryReceiptsApi } from '../../api/deliveryReceipts'
import { Button, Callout, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  saleId: string
  deliveryReceiptId: string
  sequenceNumber: number
}

export function CancelDeliveryModal({ open, onClose, saleId, deliveryReceiptId, sequenceNumber }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const [reason, setReason] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setReason('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () => deliveryReceiptsApi.cancel(deliveryReceiptId, { reason }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-summary'] })
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-receipts'] })
      toast('success', `Delivery ${sequenceNumber} cancelled`)
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'DELIVERY_RECEIPT_CONCURRENCY_CONFLICT') {
        setError('This delivery was changed by someone else. Close this dialog and try again.')
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not cancel this delivery.')
    },
  })

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`Cancel Delivery ${sequenceNumber}`}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Keep it
          </Button>
          <Button
            size="sm"
            variant="destructive"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!reason.trim()}
          >
            Cancel delivery
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}
      <p className="mb-3 text-[13px] text-text-muted">
        This releases its items back to Unscheduled so they can be put on a new delivery. The Sale
        itself and its delivery charge are unaffected. This record is kept as history and cannot be
        reactivated — scheduling again creates a new one.
      </p>
      <TextField
        label="Reason"
        name="reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        autoFocus
      />
    </Modal>
  )
}
```

- [ ] **Step 6: Rewrite the delivery section of `SaleDetailPage.tsx`**

Replace the `deliveryReceiptQuery`/`deliveryReceiptOpen` state and its JSX block. Import changes:

```tsx
import { CancelDeliveryModal } from '../components/sales/CancelDeliveryModal'
import { CreateDeliveryReceiptModal, type DeliveryPrefill } from '../components/sales/CreateDeliveryReceiptModal'
import { DeliveryStatusBadge } from '../components/sales/DeliveryStatusBadge'
import { formatQty } from '../lib/format'
import { SALE_FULFILLMENT_STATUS_LABELS, saleFulfillmentStatusTone } from '../lib/pos'
import { Badge, ConfirmDialog } from '../components/ui'
```

State:

```tsx
  const [createDeliveryOpen, setCreateDeliveryOpen] = useState(false)
  const [reschedulePrefill, setReschedulePrefill] = useState<DeliveryPrefill | null>(null)
  const [cancelTarget, setCancelTarget] = useState<{ id: string; sequenceNumber: number } | null>(null)
  const [deliverTarget, setDeliverTarget] = useState<{ id: string; sequenceNumber: number } | null>(null)
```

Replace the `deliveryReceiptQuery` with:

```tsx
  const deliverySummaryQuery = useQuery({
    queryKey: ['sales', id, 'delivery-summary'],
    queryFn: () => deliveryReceiptsApi.getSaleSummary(id),
    enabled: !!id,
  })

  const markDeliveredMutation = useMutation({
    mutationFn: (deliveryReceiptId: string) => deliveryReceiptsApi.markDelivered(deliveryReceiptId),
    onSuccess: () => {
      deliverySummaryQuery.refetch()
      setDeliverTarget(null)
    },
  })
```

(Add `useMutation` to the existing `@tanstack/react-query` import if not already there — `SaleDetailPage.tsx` currently only imports `useQuery`.)

Replace the old delivery-receipt button block (the `deliveryReceipt ? ... : canPrintDeliveryReceipt ? ...` ternary in the header actions) — delivery actions move out of the header entirely into their own section below, since there can now be several deliveries with different states. Remove that ternary and its `deliveryReceiptQuery`/`canPrintDeliveryReceipt` references.

Add a new section, placed after the Payments card (inside the `space-y-4` column, or as its own full-width block below the two-column grid — either is fine, match the page's existing spacing conventions):

```tsx
                {deliverySummaryQuery.data && deliverySummaryQuery.data.fulfillmentStatus !== 'NotApplicable' && (
                  <div className="space-y-3">
                    <div className="flex items-center justify-between">
                      <h2 className="text-lg font-bold text-text-primary">Delivery fulfillment</h2>
                      <Badge tone={saleFulfillmentStatusTone(deliverySummaryQuery.data.fulfillmentStatus)}>
                        {SALE_FULFILLMENT_STATUS_LABELS[deliverySummaryQuery.data.fulfillmentStatus]}
                      </Badge>
                    </div>

                    <div className="overflow-x-auto rounded-xl border border-border bg-surface">
                      <table className="w-full text-left text-[13px]">
                        <thead className="border-b border-border text-text-muted">
                          <tr>
                            <th className="px-3 py-2 font-medium">Item</th>
                            <th className="px-3 py-2 text-right font-medium">Sold</th>
                            <th className="px-3 py-2 text-right font-medium">Take-now</th>
                            <th className="px-3 py-2 text-right font-medium">Required</th>
                            <th className="px-3 py-2 text-right font-medium">Pending</th>
                            <th className="px-3 py-2 text-right font-medium">Delivered</th>
                            <th className="px-3 py-2 text-right font-medium">Available</th>
                          </tr>
                        </thead>
                        <tbody>
                          {deliverySummaryQuery.data.items.map((i) => (
                            <tr key={i.saleItemId} className="border-b border-border-light last:border-0">
                              <td className="px-3 py-2 text-text-primary">
                                {i.productName}
                                {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
                              </td>
                              <td className="px-3 py-2 text-right">{formatQty(i.quantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.takeNowQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.deliveryRequiredQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.pendingQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.deliveredQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.availableToScheduleQuantity)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>

                    {deliverySummaryQuery.data.canCreateDelivery ? (
                      <Button variant="secondary" size="sm" onClick={() => { setReschedulePrefill(null); setCreateDeliveryOpen(true) }}>
                        Create delivery
                      </Button>
                    ) : (
                      <p className="text-[13px] text-text-muted">
                        All delivery items have already been scheduled or delivered.
                      </p>
                    )}

                    <div className="space-y-2">
                      {deliverySummaryQuery.data.deliveries.map((dr) => (
                        <div key={dr.id} className="rounded-xl border border-border bg-surface p-3">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <div className="flex items-center gap-2">
                              <span className="text-sm font-semibold text-text-primary">Delivery {dr.sequenceNumber}</span>
                              <DeliveryStatusBadge status={dr.status} />
                              <span className="text-[12px] text-text-muted">
                                {new Date(dr.scheduledDeliveryDate).toLocaleDateString()}
                              </span>
                            </div>
                            <div className="flex gap-2">
                              <Button
                                variant="secondary"
                                size="sm"
                                onClick={() => window.open(`/delivery-receipts/${dr.id}?print=1`, '_blank', 'noopener')}
                              >
                                View
                              </Button>
                              {dr.status === 'Pending' && (
                                <Button size="sm" onClick={() => setDeliverTarget({ id: dr.id, sequenceNumber: dr.sequenceNumber })}>
                                  Mark delivered
                                </Button>
                              )}
                              {dr.status === 'Pending' && canCancelDelivery && (
                                <Button
                                  variant="destructive"
                                  size="sm"
                                  onClick={() => setCancelTarget({ id: dr.id, sequenceNumber: dr.sequenceNumber })}
                                >
                                  Cancel
                                </Button>
                              )}
                              {dr.status === 'Cancelled' && deliverySummaryQuery.data!.canCreateDelivery && (
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  onClick={() => {
                                    setReschedulePrefill({
                                      recipientName: dr.recipientName,
                                      deliveryAddress: dr.deliveryAddress,
                                      contactNumber: dr.contactNumber ?? '',
                                      deliveryNotes: dr.deliveryNotes ?? '',
                                      itemQuantities: Object.fromEntries(dr.items.map((i) => [i.saleItemId, i.quantity])),
                                    })
                                    setCreateDeliveryOpen(true)
                                  }}
                                >
                                  Schedule again
                                </Button>
                              )}
                            </div>
                          </div>
                          <p className="mt-1 text-[13px] text-text-secondary">
                            {dr.recipientName} · {dr.deliveryAddress}
                          </p>
                          {dr.status === 'Cancelled' && dr.cancellationReason && (
                            <p className="mt-1 text-[12px] text-text-muted">Cancelled: {dr.cancellationReason}</p>
                          )}
                        </div>
                      ))}
                    </div>
                  </div>
                )}
```

Add `const canCancelDelivery = useCan('delivery:cancel')` near the other `useCan` calls at the top of the component.

Add the three modals near the existing `ReturnModal`/`VoidSaleModal` JSX at the bottom:

```tsx
                <CreateDeliveryReceiptModal
                  open={createDeliveryOpen}
                  onClose={() => setCreateDeliveryOpen(false)}
                  saleId={d.sale.id}
                  availableItems={(deliverySummaryQuery.data?.items ?? []).filter((i) => i.availableToScheduleQuantity > 0)}
                  prefill={reschedulePrefill ?? undefined}
                />
                {cancelTarget && (
                  <CancelDeliveryModal
                    open
                    onClose={() => setCancelTarget(null)}
                    saleId={d.sale.id}
                    deliveryReceiptId={cancelTarget.id}
                    sequenceNumber={cancelTarget.sequenceNumber}
                  />
                )}
                <ConfirmDialog
                  open={deliverTarget != null}
                  onClose={() => setDeliverTarget(null)}
                  onConfirm={() => deliverTarget && markDeliveredMutation.mutate(deliverTarget.id)}
                  title={`Mark Delivery ${deliverTarget?.sequenceNumber ?? ''} as delivered?`}
                  message="This completes every item on this delivery. It cannot be undone from here — a mistaken delivery would need to be corrected as a fresh workflow, not reopened."
                  confirmLabel="Mark delivered"
                  loading={markDeliveredMutation.isPending}
                />
```

Remove `deliveryReceiptsApi.getForSale`/`createForSale` references entirely from this file (superseded).

- [ ] **Step 7: Type-check and manually verify**

Run: `npx tsc -b` (from `web/negosio-web`) — this file and `CreateDeliveryReceiptModal.tsx` should now be clean; `DeliveryReceiptPage.tsx` and `DeliveryReportsPage.tsx` are still expected to fail (Tasks 17-18).

Manually verify in the dev server: complete a delivery sale from Task 15's flow, open its Sale-detail page, confirm the fulfillment table and delivery list render correctly, mark one delivery delivered, cancel another, and use "Schedule again" on the cancelled one to confirm it creates a new record (next sequence number) rather than reactivating the old one. Report the actual outcome.

- [ ] **Step 8: Commit**

```bash
git add web/negosio-web/src/lib/useCan.ts web/negosio-web/src/lib/pos.ts web/negosio-web/src/components/sales/CreateDeliveryReceiptModal.tsx web/negosio-web/src/components/sales/CancelDeliveryModal.tsx web/negosio-web/src/components/sales/DeliveryStatusBadge.tsx web/negosio-web/src/pages/SaleDetailPage.tsx
git commit -m "feat(web): Sale-detail fulfillment breakdown and delivery actions"
```

---

### Task 17: Delivery receipt print page — sequence, scheduled date, status, audit info

**Files:**
- Modify: `web/negosio-web/src/pages/DeliveryReceiptPage.tsx`

**Interfaces:**
- Consumes: `DeliveryReceiptDto`'s new fields (Task 14) — `sequenceNumber`, `scheduledDeliveryDate`, `status`, `deliveredAtUtc`/`deliveredByName`, `cancelledAtUtc`/`cancelledByName`/`cancellationReason`.
- Produces: nothing consumed elsewhere — this is a leaf page.

No automated test. Verified by `npx tsc -b` plus a print-preview check in the browser.

- [ ] **Step 1: Update the title, meta, and add a status/audit block**

Replace the `dr-title`/`dr-meta` block:

```tsx
        {!hasCustomHeader && <p className="dr-title">DELIVERY RECEIPT — Delivery {d.sequenceNumber}</p>}
        <hr />

        <div className="dr-meta">
          <span>Prepared: {when}</span>
          <span>Scheduled for: {new Date(d.scheduledDeliveryDate).toLocaleDateString()}</span>
          <span>Branch: {d.branchName}</span>
        </div>

        <div className="dr-line">
          <span className="label">Status:</span> {d.status}
        </div>
```

- [ ] **Step 2: Add a delivered/cancelled audit block**

Insert this right after the items table (before the `showPriceColumns && ...` delivery-fee line), replacing nothing — purely additive:

```tsx
        {d.status === 'Delivered' && d.deliveredAtUtc && (
          <div className="dr-line">
            <span className="label">Delivered:</span> {new Date(d.deliveredAtUtc).toLocaleString()}
            {d.deliveredByName ? ` by ${d.deliveredByName}` : ''}
          </div>
        )}
        {d.status === 'Cancelled' && d.cancelledAtUtc && (
          <div className="dr-line">
            <span className="label">Cancelled:</span> {new Date(d.cancelledAtUtc).toLocaleString()}
            {d.cancelledByName ? ` by ${d.cancelledByName}` : ''}
            {d.cancellationReason ? ` — ${d.cancellationReason}` : ''}
          </div>
        )}
```

`d.items` here is already scoped to this one delivery's own assigned quantities (unchanged from the existing implementation — a `DeliveryReceipt`'s `Items` navigation was always its own rows, never the whole sale's) — this page has satisfied "a partial Delivery Receipt must not print all items from the Sale" since before this plan; no change needed to the items table itself.

- [ ] **Step 3: Type-check and manually verify**

Run: `npx tsc -b` (from `web/negosio-web`) — this file, and every file this plan touched, should now be clean. `DeliveryReportsPage.tsx` is the last one still expected to fail (Task 18).

Open a delivery's print page (`/delivery-receipts/{id}`) in the dev server for one Pending, one Delivered, and one Cancelled delivery from Task 16's walkthrough — confirm the sequence label, scheduled date, status, and audit line render correctly and that only that delivery's own items appear (not the whole sale's). Report the actual outcome.

- [ ] **Step 4: Commit**

```bash
git add web/negosio-web/src/pages/DeliveryReceiptPage.tsx
git commit -m "feat(web): delivery receipt print page shows sequence, schedule, status, and audit info"
```

---

### Task 18: Delivery reports — extend the schedule view, add the Sale fulfillment view

**Files:**
- Modify: `web/negosio-web/src/api/reports.ts`
- Modify: `web/negosio-web/src/pages/DeliveryReportsPage.tsx` (full-file replacement)

**Interfaces:**
- Consumes: `reportsApi.deliveries` (extended params, Task 14 types), the new `reportsApi.deliveryFulfillment` this task adds, `DeliveryStatusBadge` (Task 16).
- Produces: nothing consumed elsewhere — this is the plan's last consumer-facing page.

No automated test. Verified by `npx tsc -b` plus a manual walkthrough.

- [ ] **Step 1: Add `deliveryFulfillment` to `reports.ts`**

```typescript
import type {
  CategoryPerformanceDto,
  DeliveryFulfillmentReportParams,
  DeliveryFulfillmentReportResultDto,
  DeliveryReportParams,
  DeliveryReportResultDto,
  ReportFilterParams,
  ReportsOverviewDto,
  TopProductDto,
} from './types'

export const reportsApi = {
  // ...existing overview/topProducts/categories/deliveries unchanged...

  deliveryFulfillment: (params: DeliveryFulfillmentReportParams) =>
    apiRequest<DeliveryFulfillmentReportResultDto>(`/api/reports/delivery-fulfillment${qs({ ...params })}`),
}
```

(Add the import, add the one new method to the existing object — do not restate the three unchanged methods verbatim if the file already has them; this is additive.)

- [ ] **Step 2: Replace `DeliveryReportsPage.tsx` in full**

```tsx
import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Banknote, Gift, PackageCheck, Percent, Truck } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { reportsApi } from '../api/reports'
import type { DeliveryReportPreset, DeliveryStatus, SaleFulfillmentStatus } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { formatDeliveryCharge, formatMoney, formatQty } from '../lib/format'
import { SALE_FULFILLMENT_STATUS_LABELS, saleFulfillmentStatusTone } from '../lib/pos'
import { DeliveryStatusBadge } from '../components/sales/DeliveryStatusBadge'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
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

const PRESETS: { value: DeliveryReportPreset; label: string }[] = [
  { value: 'All', label: 'All' },
  { value: 'Today', label: 'Today' },
  { value: 'Upcoming', label: 'Upcoming' },
  { value: 'Overdue', label: 'Overdue' },
  { value: 'Delivered', label: 'Delivered' },
  { value: 'Cancelled', label: 'Cancelled' },
  { value: 'NeedsRescheduling', label: 'Needs rescheduling' },
]

type ScheduleFilters = { branchId: string | undefined; preset: DeliveryReportPreset | undefined }
const SCHEDULE_DEFAULT_FILTERS: ScheduleFilters = { branchId: undefined, preset: undefined }

type FulfillmentFilters = { branchId: string | undefined; status: SaleFulfillmentStatus | undefined }
const FULFILLMENT_DEFAULT_FILTERS: FulfillmentFilters = { branchId: undefined, status: undefined }

export default function DeliveryReportsPage() {
  const [view, setView] = useState<'schedule' | 'fulfillment'>('schedule')

  const branchesQuery = useQuery({
    queryKey: ['branches', 'delivery-reports-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const branches = branchesQuery.data ?? []
  const multiBranch = branches.length > 1

  const schedule = usePagedQuery<ScheduleFilters>({ defaultFilters: SCHEDULE_DEFAULT_FILTERS })
  const scheduleQuery = useQuery({
    queryKey: [
      'reports',
      'deliveries',
      { page: schedule.page, search: schedule.search, branchId: schedule.filters.branchId, preset: schedule.filters.preset },
    ],
    queryFn: () =>
      reportsApi.deliveries({
        page: schedule.page,
        pageSize: schedule.pageSize,
        search: schedule.search || undefined,
        branchId: schedule.filters.branchId,
        preset: schedule.filters.preset,
      }),
    enabled: view === 'schedule',
  })

  const fulfillment = usePagedQuery<FulfillmentFilters>({ defaultFilters: FULFILLMENT_DEFAULT_FILTERS })
  const fulfillmentQuery = useQuery({
    queryKey: [
      'reports',
      'delivery-fulfillment',
      { page: fulfillment.page, search: fulfillment.search, branchId: fulfillment.filters.branchId, status: fulfillment.filters.status },
    ],
    queryFn: () =>
      reportsApi.deliveryFulfillment({
        page: fulfillment.page,
        pageSize: fulfillment.pageSize,
        search: fulfillment.search || undefined,
        branchId: fulfillment.filters.branchId,
        status: fulfillment.filters.status,
      }),
    enabled: view === 'fulfillment',
  })

  return (
    <DashboardLayout title="Delivery Reports">
      <div className="space-y-5">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Delivery Reports</h1>
          <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
            <button
              type="button"
              onClick={() => setView('schedule')}
              className={`rounded-md px-3 py-1.5 text-[13px] font-semibold ${view === 'schedule' ? 'bg-white text-text-primary shadow-sm' : 'text-text-muted'}`}
            >
              Delivery schedule
            </button>
            <button
              type="button"
              onClick={() => setView('fulfillment')}
              className={`rounded-md px-3 py-1.5 text-[13px] font-semibold ${view === 'fulfillment' ? 'bg-white text-text-primary shadow-sm' : 'text-text-muted'}`}
            >
              Sale fulfillment
            </button>
          </div>
        </div>

        {view === 'schedule' ? (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
              {scheduleQuery.isPending ? (
                Array.from({ length: 5 }).map((_, i) => <SkeletonCard key={i} />)
              ) : (
                <>
                  <MetricCard icon={Truck} accent="blue" label="Schedules" value={scheduleQuery.data?.totals.totalSchedules ?? 0} />
                  <MetricCard icon={PackageCheck} accent="purple" label="Distinct sales" value={scheduleQuery.data?.totals.distinctSalesCount ?? 0} />
                  <MetricCard icon={Gift} accent="green" label="Free-delivery sales" value={scheduleQuery.data?.totals.freeDeliverySalesCount ?? 0} />
                  <MetricCard
                    icon={Banknote}
                    accent="amber"
                    label="Total charges collected"
                    value={formatMoney(scheduleQuery.data?.totals.totalDeliveryCharges ?? 0)}
                  />
                  <MetricCard
                    icon={Percent}
                    accent="red"
                    label="Average charge / sale"
                    value={formatMoney(scheduleQuery.data?.totals.averageDeliveryChargePerSale ?? 0)}
                  />
                </>
              )}
            </div>

            <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
              <SearchInput
                className="sm:max-w-xs"
                value={schedule.searchInput}
                onChange={schedule.setSearchInput}
                placeholder="Search by sale #, recipient, address, or contact number"
              />
              {multiBranch && (
                <Select
                  aria-label="Branch"
                  className="sm:max-w-[12rem]"
                  value={schedule.filters.branchId ?? ''}
                  onChange={(e) => schedule.setFilter('branchId', e.target.value || undefined)}
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
              <Select
                aria-label="Filter"
                className="sm:max-w-[12rem]"
                value={schedule.filters.preset ?? 'All'}
                onChange={(e) => schedule.setFilter('preset', (e.target.value as DeliveryReportPreset) || undefined)}
              >
                {PRESETS.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </Select>
            </div>

            {scheduleQuery.isError ? (
              <ErrorState message={(scheduleQuery.error as Error).message} onRetry={() => scheduleQuery.refetch()} />
            ) : scheduleQuery.isPending ? (
              <Table>
                <Table.Head>
                  <Table.HeaderCell>Delivery</Table.HeaderCell>
                  <Table.HeaderCell>Sale #</Table.HeaderCell>
                  <Table.HeaderCell>Scheduled</Table.HeaderCell>
                  <Table.HeaderCell>Status</Table.HeaderCell>
                  <Table.HeaderCell>Recipient</Table.HeaderCell>
                  <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                  <Table.HeaderCell>Prepared by</Table.HeaderCell>
                </Table.Head>
                <Table.Body>
                  {Array.from({ length: 6 }).map((_, i) => (
                    <Table.Row key={i}>
                      {Array.from({ length: 7 }).map((__, j) => (
                        <Table.Cell key={j}>
                          <SkeletonText className={j === 0 ? 'w-24' : 'w-20'} />
                        </Table.Cell>
                      ))}
                    </Table.Row>
                  ))}
                </Table.Body>
              </Table>
            ) : scheduleQuery.data.page.items.length === 0 ? (
              <EmptyState
                icon={Truck}
                title="No deliveries match these filters"
                description="Try clearing the search or filter."
              />
            ) : (
              <>
                <Table>
                  <Table.Head>
                    <Table.HeaderCell>Delivery</Table.HeaderCell>
                    <Table.HeaderCell>Sale #</Table.HeaderCell>
                    <Table.HeaderCell>Scheduled</Table.HeaderCell>
                    <Table.HeaderCell>Status</Table.HeaderCell>
                    <Table.HeaderCell>Recipient</Table.HeaderCell>
                    <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                    <Table.HeaderCell>Prepared by</Table.HeaderCell>
                  </Table.Head>
                  <Table.Body>
                    {scheduleQuery.data.page.items.map((r) => (
                      <Table.Row key={r.deliveryReceiptId} className={r.isOverdue ? 'bg-danger-light/40' : undefined}>
                        <Table.Cell>Delivery {r.sequenceNumber}</Table.Cell>
                        <Table.Cell>
                          <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                            #{r.saleNumber}
                          </Link>
                        </Table.Cell>
                        <Table.Cell>{new Date(r.scheduledDeliveryDate).toLocaleDateString()}</Table.Cell>
                        <Table.Cell>
                          <DeliveryStatusBadge status={r.status} />
                          {r.isOverdue && <span className="ml-1.5 text-[11px] font-semibold text-danger">Overdue</span>}
                        </Table.Cell>
                        <Table.Cell>{r.recipientName}</Table.Cell>
                        <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                        <Table.Cell>{r.preparedByName}</Table.Cell>
                      </Table.Row>
                    ))}
                  </Table.Body>
                </Table>
                <Pagination
                  page={scheduleQuery.data.page.page}
                  pageSize={scheduleQuery.data.page.pageSize}
                  totalCount={scheduleQuery.data.page.totalCount}
                  totalPages={scheduleQuery.data.page.totalPages}
                  onPageChange={schedule.setPage}
                />
              </>
            )}
          </>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-2 lg:grid-cols-2">
              {fulfillmentQuery.isPending ? (
                <>
                  <SkeletonCard />
                  <SkeletonCard />
                </>
              ) : (
                <>
                  <MetricCard icon={PackageCheck} accent="blue" label="Sales with a delivery component" value={fulfillmentQuery.data?.totals.totalSales ?? 0} />
                  <MetricCard icon={Banknote} accent="amber" label="Total delivery charges" value={formatMoney(fulfillmentQuery.data?.totals.totalDeliveryCharges ?? 0)} />
                </>
              )}
            </div>

            <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
              <SearchInput
                className="sm:max-w-xs"
                value={fulfillment.searchInput}
                onChange={fulfillment.setSearchInput}
                placeholder="Search by sale #"
              />
              {multiBranch && (
                <Select
                  aria-label="Branch"
                  className="sm:max-w-[12rem]"
                  value={fulfillment.filters.branchId ?? ''}
                  onChange={(e) => fulfillment.setFilter('branchId', e.target.value || undefined)}
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
              <Select
                aria-label="Fulfillment status"
                className="sm:max-w-[14rem]"
                value={fulfillment.filters.status ?? ''}
                onChange={(e) => fulfillment.setFilter('status', (e.target.value as SaleFulfillmentStatus) || undefined)}
              >
                <option value="">All statuses</option>
                {(Object.keys(SALE_FULFILLMENT_STATUS_LABELS) as SaleFulfillmentStatus[])
                  .filter((s) => s !== 'NotApplicable')
                  .map((s) => (
                    <option key={s} value={s}>
                      {SALE_FULFILLMENT_STATUS_LABELS[s]}
                    </option>
                  ))}
              </Select>
            </div>

            {fulfillmentQuery.isError ? (
              <ErrorState message={(fulfillmentQuery.error as Error).message} onRetry={() => fulfillmentQuery.refetch()} />
            ) : fulfillmentQuery.isPending ? (
              <Table>
                <Table.Head>
                  <Table.HeaderCell>Sale #</Table.HeaderCell>
                  <Table.HeaderCell>Created</Table.HeaderCell>
                  <Table.HeaderCell>Status</Table.HeaderCell>
                  <Table.HeaderCell align="right">Required</Table.HeaderCell>
                  <Table.HeaderCell align="right">Pending</Table.HeaderCell>
                  <Table.HeaderCell align="right">Delivered</Table.HeaderCell>
                  <Table.HeaderCell align="right">Unscheduled</Table.HeaderCell>
                  <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                </Table.Head>
                <Table.Body>
                  {Array.from({ length: 6 }).map((_, i) => (
                    <Table.Row key={i}>
                      {Array.from({ length: 8 }).map((__, j) => (
                        <Table.Cell key={j}>
                          <SkeletonText className={j === 0 ? 'w-24' : 'w-16'} />
                        </Table.Cell>
                      ))}
                    </Table.Row>
                  ))}
                </Table.Body>
              </Table>
            ) : fulfillmentQuery.data.page.items.length === 0 ? (
              <EmptyState
                icon={PackageCheck}
                title="No sales match these filters"
                description="Sales with at least one item marked for delivery will show here."
              />
            ) : (
              <>
                <Table>
                  <Table.Head>
                    <Table.HeaderCell>Sale #</Table.HeaderCell>
                    <Table.HeaderCell>Created</Table.HeaderCell>
                    <Table.HeaderCell>Status</Table.HeaderCell>
                    <Table.HeaderCell align="right">Required</Table.HeaderCell>
                    <Table.HeaderCell align="right">Pending</Table.HeaderCell>
                    <Table.HeaderCell align="right">Delivered</Table.HeaderCell>
                    <Table.HeaderCell align="right">Unscheduled</Table.HeaderCell>
                    <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                  </Table.Head>
                  <Table.Body>
                    {fulfillmentQuery.data.page.items.map((r) => (
                      <Table.Row key={r.saleId}>
                        <Table.Cell>
                          <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                            #{r.saleNumber}
                          </Link>
                        </Table.Cell>
                        <Table.Cell>{new Date(r.saleCreatedAtUtc).toLocaleDateString()}</Table.Cell>
                        <Table.Cell>
                          <Badge tone={saleFulfillmentStatusTone(r.fulfillmentStatus)}>
                            {SALE_FULFILLMENT_STATUS_LABELS[r.fulfillmentStatus]}
                          </Badge>
                        </Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalDeliveryRequiredQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalPendingQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalDeliveredQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalUnscheduledQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                      </Table.Row>
                    ))}
                  </Table.Body>
                </Table>
                <Pagination
                  page={fulfillmentQuery.data.page.page}
                  pageSize={fulfillmentQuery.data.page.pageSize}
                  totalCount={fulfillmentQuery.data.page.totalCount}
                  totalPages={fulfillmentQuery.data.page.totalPages}
                  onPageChange={fulfillment.setPage}
                />
              </>
            )}
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
```

Confirm `Table.Row` actually accepts a `className` prop (used above for the overdue-row highlight) by checking `components/ui/Table.tsx` first — if it doesn't, add the prop there (a small, additive change) rather than inventing a different mechanism; this is the plan's one new "row highlight" convention the Global Constraints flagged as new ground.

Confirm `usePagedQuery`'s actual field names (`searchInput`/`setSearchInput`/`search`/`filters`/`setFilter`/`page`/`pageSize`/`setPage`) by reading `hooks/usePagedQuery.ts` first — used here exactly as `SalesPage.tsx`/the pre-existing `DeliveryReportsPage.tsx` already used it, so this should already match, but confirm before assuming.

- [ ] **Step 3: Type-check and manually verify**

Run: `npx tsc -b` (from `web/negosio-web`) — this should be the LAST file with errors; a clean run here means every file this plan touched compiles.

Manually verify in the dev server: both tabs load, the schedule view's overdue highlight/preset filters work (you can force an overdue row via the same raw-SQL `ScheduledDeliveryDate` trick Task 11's backend test used, or just trust Task 11's own passing test for that specific mechanism and verify the UI wiring against a Today/Upcoming/Delivered/Cancelled row instead), and the fulfillment view's totals match what Task 11's own report test already proved (charge counted once per sale, not per schedule). Report the actual outcome.

- [ ] **Step 4: Commit**

```bash
git add web/negosio-web/src/api/reports.ts web/negosio-web/src/pages/DeliveryReportsPage.tsx
git commit -m "feat(web): delivery reports — extend the schedule view, add the Sale fulfillment view"
```

---

### Task 19: Final verification

**Files:** none (verification-only; fix forward wherever a failure is found).

**Interfaces:** none — this is the plan's closing gate.

- [ ] **Step 1: Full backend regression**

Run: `dotnet test`
Expected: every test passes (all pre-existing suites plus every test Tasks 1-3, 7, 9-12 added).

- [ ] **Step 2: Full backend build**

Run: `dotnet build Negosio.sln`
Expected: 0 errors.

- [ ] **Step 3: Frontend type-check**

Run: `npx tsc -b` from `web/negosio-web`
Expected: 0 errors, across the whole project (not just the files this plan touched — a change to a shared type like `SaleItemDto` can ripple).

- [ ] **Step 4: Frontend production build**

Run: `npm run build` from `web/negosio-web`
Expected: builds cleanly. This is the one check that isn't `tsc -b`-redundant — it also runs Vite's own bundling, which can surface issues `tsc` alone doesn't (e.g. an unresolved import path).

- [ ] **Step 5: Live smoke test**

Start both dev servers and, in a real browser, run through the full happy path once end-to-end without stopping to fix anything cosmetic along the way (note any issue found, fix it after this pass, then re-verify just the affected step):
1. POS: sell a 3-line cart, mark one line fully for delivery and one line partially for delivery, add a second delivery schedule splitting the partial line's quantity across both schedules with different dates, complete checkout with cash.
2. Confirm the success modal shows "View 2 delivery schedules" and the receipt/sale total is unaffected by having two schedules (delivery charge applied once).
3. Open the Sale-detail page: confirm the fulfillment table and both delivery cards render, "Create delivery" is disabled/hidden once fully scheduled (or shows the "already scheduled" message).
4. Mark one delivery Delivered, cancel the other with a reason, then use "Schedule again" on the cancelled one and confirm it creates Delivery 3 (not reactivating Delivery 2) with the released quantity.
5. Print each of the three delivery receipts — confirm each shows only its own items, the correct sequence label, scheduled date, and status/audit info.
6. Open Delivery Reports: confirm the new schedule appears correctly (status, overdue/preset filters), switch to Sale fulfillment view, confirm the sale's totals match (delivery charge shown once, not tripled across 3 schedules).
7. Repeat a normal delivery-charge-only sale (no items marked for delivery) exactly as in the original delivery-charge feature, to confirm that flow is completely unaffected — this is the plan's most important regression check, since preserving it was an explicit, repeated user instruction.

- [ ] **Step 6: Final commit (if Steps 1-5 required any fix)**

```bash
git add -A
git commit -m "fix(delivery): final verification fixes"
```

If nothing needed fixing, there is nothing to commit — the plan is complete. Do not merge or push (per the plan's Global Constraints and the project owner's explicit instruction) — leave the branch as-is for review.
