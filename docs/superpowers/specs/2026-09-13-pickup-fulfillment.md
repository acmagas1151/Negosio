# Spec — Pickup fulfillment method + cross-method conversions

_Given verbatim by the project owner on 2026-09-13. This is the binding authority for
`docs/superpowers/plans/2026-09-13-pickup-fulfillment.md`; where the plan and this spec disagree,
this spec wins._

Context given with the spec: the following are already implemented and currently being tested —
For delivery during POS checkout; `Sales.DeliveryCharge`; scheduled delivery dates; multiple
deliveries for one Sale; partial delivery by Sale item and quantity; Pending/Delivered/Cancelled
delivery statuses; cancelling and rescheduling deliveries; delivery reports; sale fulfillment
reporting. Preserve the existing working behavior and extend it with the finalized fulfillment-method
design below. Do not merge or push changes.

## Finalized terminology

Fulfillment method and fulfillment status are different concepts.

### Fulfillment methods

```csharp
public enum FulfillmentMethod
{
    TakeNow,
    Delivery,
    Pickup
}
```

- `TakeNow`: the customer receives the item during the original POS checkout.
- `Delivery`: the business transports the item to the customer.
- `Pickup`: the customer collects the item after the original checkout.

TakeNow is the default during checkout.

TakeNow is strictly a checkout-time fulfillment method. It must not be used as a post-sale
cancellation disposition or as a replacement for a successful Pickup.

### Fulfillment statuses

```csharp
public enum FulfillmentStatus
{
    Unscheduled,
    Pending,
    Completed,
    Cancelled
}
```

Method-specific UI labels:

| Method   | Internal status | UI label             |
| -------- | --------------- | -------------------- |
| TakeNow  | Completed       | Taken now            |
| Delivery | Unscheduled     | Deliver later        |
| Delivery | Pending         | Pending delivery     |
| Delivery | Completed       | Delivered            |
| Delivery | Cancelled       | Cancelled delivery   |
| Pickup   | Unscheduled     | Pickup not scheduled |
| Pickup   | Pending         | Pending pickup       |
| Pickup   | Completed       | Claimed              |
| Pickup   | Cancelled       | Cancelled pickup     |

Important semantic rules:

- Delivered means the business successfully delivered the items.
- Claimed means the customer successfully collected the items after checkout.
- TakeNow means the customer received the items during checkout.
- Cancelled means the planned Delivery or Pickup did not happen.
- A successful Pickup must be marked Claimed, not Cancelled or TakeNow.
- A customer collecting an originally scheduled Delivery must produce:
  - A Cancelled Delivery record
  - A successful Completed/Claimed Pickup record

## Phase 1: inspect before editing

Inspect: Sale and SaleItem fulfillment fields; existing `DeliveryRequiredQuantity`; DeliveryReceipt
and DeliveryReceiptItem; delivery status transitions; delivery cancellation dispositions; delivery
rescheduling; current allocation formulas; checkout contracts; sale details; delivery reports;
delivery-charge behavior; auditing infrastructure; tenant isolation; authorization policies; EF Core
migrations; concurrency handling; business timezone handling.

Briefly summarize the current implementation before editing.

Choose the smallest clean architecture for Pickup support. Do not create conflicting sources of truth
or perform a large rewrite solely for architectural elegance.

## Fulfillment allocation

Every sold quantity must belong to exactly one original checkout fulfillment method:

```text
Sold quantity = Take-now quantity + Delivery-intended quantity + Pickup-intended quantity
```

```text
Delivery-intended quantity = Delivery-unscheduled + Delivery-pending + Delivery-completed
Pickup-intended quantity   = Pickup-unscheduled   + Pickup-pending   + Pickup-completed
```

Cancelled schedule quantities do not consume an active schedule. Their approved disposition
determines their next method or state.

Rules:

- TakeNow is the default during checkout.
- TakeNow is completed immediately.
- TakeNow quantities cannot be scheduled for Delivery or Pickup later.
- Pending Delivery quantities cannot be allocated elsewhere.
- Delivered quantities cannot be allocated again.
- Pending Pickup quantities cannot be allocated elsewhere.
- Claimed quantities cannot be allocated again.
- Delivery-unscheduled quantities can be included in a future Delivery.
- Pickup-unscheduled quantities can be included in a future Pickup.
- All allocation totals must remain within the SaleItem's sold quantity.
- Backend calculations are authoritative.
- Do not rely solely on frontend validation.

Preserve the original checkout allocation for audit purposes. Post-sale conversions between Delivery
and Pickup must be recorded as explicit, immutable fulfillment events or adjustments.

## Recommended data-model direction

Inspect the code and choose the least disruptive normalized approach. Acceptable approaches include:

1. Extending SaleItem with Pickup-intended quantity while retaining the existing Delivery-required
   quantity and adding audited conversion records.
2. Introducing a normalized SaleItem fulfillment-allocation model shared by Delivery and Pickup.
3. Generalizing the existing Delivery schedule aggregate into a Fulfillment schedule with a Method
   discriminator, only if this can be done safely without destabilizing existing functionality.

Regardless of implementation:

- There must be one source of truth for every quantity.
- Original checkout allocation must remain reconstructable.
- Post-sale conversions must be auditable.
- Existing Delivery records must remain valid.
- Existing Delivery reports must continue working.
- Tenant isolation must be preserved.
- Concurrent actions must not over-allocate quantities.

Before implementation, state which approach you selected and why.

## POS checkout

For each Sale item, default the full sold quantity to TakeNow. When the cashier changes fulfillment,
allow quantities to be split across Take now / For delivery / For pickup.

```text
Sprite — Sold: 8
Take now:       2
For delivery:   4
For pickup:     2
```

Require `Take now + For delivery + For pickup = Sold quantity`. Block checkout when the allocation is
invalid, but show clear inline errors.

### Take now

- Requires no schedule. Requires no customer contact or address. Is completed during checkout.
- Does not appear as Pending in Delivery or Pickup reports.
- Cannot be chosen as a post-sale conversion target.

### For delivery

Preserve the existing behavior: recipient name; delivery address; contact number; notes; today or
future scheduled date; partial quantities; multiple delivery schedules; unscheduled Deliver-later
quantities; delivery charge stored once on the Sale.

### For pickup

Add support for: customer/recipient name; contact number; expected pickup date; optional notes;
selected Sale items and quantities; multiple Pickup schedules; unscheduled Pickup-later quantities.

Pickup does not require a delivery address. Allow today or a future expected pickup date.

Preserve all fulfillment state if checkout fails. Reset state only after successful checkout, New
Transaction, or an explicit reset.

## Pickup schedule model

Using the selected architecture, create a Pickup schedule or generalized fulfillment schedule
containing information equivalent to:

```text
Id, SaleId, SequenceNumber, ExpectedPickupDate, Status,
CustomerName, ContactNumber, Notes,
CreatedAt, CreatedByUserId,
ClaimedAt, ClaimedByUserId,
CancelledAt, CancelledByUserId, CancellationReason, CancellationDisposition
```

Child item records equivalent to: `PickupItem { Id, PickupId, SaleItemId, Quantity }`.

If a generalized fulfillment schedule already provides these safely, reuse it instead of creating
redundant tables.

Rules:

- Pickup must contain at least one item.
- Quantities must be positive.
- Pickup items must belong to the related Sale.
- A SaleItem cannot appear twice within one Pickup.
- Pending plus Completed quantities cannot exceed Pickup availability.
- Sale and items must belong to the current tenant.
- Sequence number is scoped to the Sale or fulfillment method.
- Do not create a global Pickup Receipt number unless an existing business convention requires one.

## Pickup lifecycle

### Mark as claimed

When the customer collects a Pending Pickup, the cashier uses `Mark as claimed`. On confirmation:

- Validate that the Pickup is still Pending.
- Set status to Completed.
- Set `ClaimedAt` to the current UTC timestamp.
- Record `ClaimedByUserId`.
- Treat every assigned item quantity as successfully fulfilled through Pickup.
- Prevent the quantities from being scheduled again.
- Refresh Sale fulfillment details and reports.

A Claimed Pickup is successful and terminal. Do not cancel the Pickup and do not convert it to
TakeNow.

### Cancel pickup

A Pickup should be cancelled only when the planned Pickup will not happen. Require a cancellation
reason and an item disposition.

Valid cancellation choices:

1. **Pickup later** — cancel the current Pickup; preserve it as history; return its quantities to
   Pickup Unscheduled; allow a new Pickup to be scheduled later.
2. **Convert to delivery** — cancel the current Pickup; preserve it as history; convert the released
   quantities from Pickup intent to Delivery intent; require or confirm recipient, delivery address,
   contact number, delivery date, notes; create a new Pending Delivery schedule; perform the
   cancellation, conversion, and new Delivery creation atomically.

There must be no TakeNow choice when cancelling a Pickup. If the customer is physically receiving the
items, use Mark as claimed instead.

## Delivery cancellation

When cancelling a Pending Delivery, require a cancellation reason and an item disposition.

Valid outcomes:

1. **Deliver later** — cancel the current Delivery; preserve it as history; return its quantities to
   Delivery Unscheduled; allow those quantities to be scheduled in another Delivery.
2. **Convert to pickup for a future date** — cancel the current Delivery; preserve it as history;
   convert its quantities from Delivery intent to Pickup intent; require customer name, contact
   number, expected pickup date, optional notes; create a new Pending Pickup schedule; perform the
   cancellation, conversion, and Pickup creation atomically.
3. **Customer picked up instead** — a clearly labeled action or cancellation outcome meaning the
   customer has already collected the items after checkout. Perform atomically:
   1. Cancel the Pending Delivery.
   2. Record its disposition as ConvertedToPickup or PickedUpInstead.
   3. Preserve the Delivery and its items as cancelled history.
   4. Convert the quantities from Delivery to Pickup fulfillment.
   5. Create a separate Pickup record.
   6. Set the new Pickup status directly to Completed.
   7. Set `ClaimedAt` to the current UTC timestamp.
   8. Record the acting user as `ClaimedByUserId`.

   Result:

   ```text
   Delivery 1 — Status: Cancelled, Disposition: CustomerPickedUpInstead
   Pickup 1   — Status: Completed, UI status: Claimed
   ```

   Do not mark the Delivery as Delivered. Do not mark the quantities as TakeNow. Do not overwrite the
   cancelled Delivery.

## Cancellation dispositions

```csharp
public enum CancellationDisposition
{
    DeliverLater,
    PickupLater,
    ConvertToDelivery,
    ConvertToPickup,
    CustomerPickedUpInstead
}
```

Only allow valid combinations:

| Source   | Allowed disposition     |
| -------- | ----------------------- |
| Delivery | DeliverLater            |
| Delivery | ConvertToPickup         |
| Delivery | CustomerPickedUpInstead |
| Pickup   | PickupLater             |
| Pickup   | ConvertToDelivery       |

Do not include TakeNow as a post-sale cancellation disposition. Store the disposition using the
project's existing enum and auditing conventions.

## Audited fulfillment conversion

Every post-sale change between Delivery and Pickup must be auditable. Add or use an immutable
event/adjustment model containing information equivalent to:

```text
Id, SaleId, SaleItemId, Quantity, FromMethod, ToMethod,
SourceRecordId, ReplacementRecordId, Reason, CreatedAt, CreatedByUserId
```

Required conversions include: Delivery to Pickup Pending; Delivery to Pickup Completed/Claimed;
Pickup to Delivery Pending; Return to Delivery Unscheduled; Return to Pickup Unscheduled.

Requirements:

- Adjustment quantities must be positive.
- Source and destination records must belong to the same Sale and tenant.
- Events must not be edited or deleted.
- Cancellation and conversion must be transactional.
- A failed replacement creation must roll back the related cancellation/conversion.
- Retried requests must not duplicate the replacement record or adjustment.

Reuse existing audit infrastructure if it satisfies these requirements.

## Creating additional schedules

**Create delivery** — enable only when Delivery Unscheduled quantity is greater than zero. Pending
and Delivered quantities are unavailable. If no quantity is available, show:

> All delivery items have already been scheduled or delivered.

**Create pickup** — enable only when Pickup Unscheduled quantity is greater than zero. Pending and
Claimed quantities are unavailable. If no quantity is available, show:

> All pickup items have already been scheduled or claimed.

## Sale details

Update Sale details to show, per Sale item: sold quantity; taken now; Delivery Unscheduled; Delivery
Pending; Delivered; Pickup Unscheduled; Pickup Pending; Claimed.

```text
Sprite — Sold: 8
Taken now:                2
Delivery pending:         2
Delivered:                1
Pickup pending:           1
Claimed:                  2
Unscheduled:              0
```

Also show: delivery schedules; pickup schedules; cancelled records; cancellation reasons;
cancellation dispositions; conversions and audit history; acting users and timestamps; delivery
charge once at Sale level.

## Delivery charge

Preserve the existing financial behavior:

- `Sales.DeliveryCharge` is stored once.
- It is included in the Sale total once.
- It is not duplicated on Delivery or Pickup records.
- Multiple schedules do not multiply it.
- Pickup does not create a delivery charge.
- Cancelling or converting fulfillment does not automatically change or refund it.
- Refunds and additional redelivery fees remain separate financial workflows.

If a completed Sale originally included a Delivery charge, later conversion to Pickup must not
silently alter its paid total.

Delivery-charge reports must aggregate distinct Sales, not joined schedule rows.

## Reporting

### Delivery report

Include only Delivery schedules: related Sale #; delivery sequence; scheduled date; status;
recipient; address; contact; items and quantities; delivered timestamp; cancelled timestamp;
cancellation reason; disposition; created and processed users; Sale delivery charge.

A Delivery cancelled because the customer collected the items must remain Cancelled in this report.
Do not count it as a successful Delivery.

### Pickup report

Include Pickup schedules: related Sale #; pickup sequence; expected pickup date; status; customer;
contact; items and quantities; claimed timestamp; cancelled timestamp; cancellation reason;
disposition; created and processed users.

A Pickup created through Customer picked up instead must appear as Completed/Claimed.

Suggested filters: Today; Upcoming; Overdue; Pending; Claimed; Cancelled; Customer; Contact; Sale
number.

### Combined fulfillment report

Include: Sale number; Sale item; quantity; fulfillment method; status; scheduled/expected date;
completion date; recipient/customer; source schedule; replacement schedule; cancellation and
conversion history.

Include summaries for: Taken now; Delivery Unscheduled; Delivery Pending; Delivered; Pickup
Unscheduled; Pickup Pending; Claimed; Cancelled schedules; fully fulfilled Sales; Sales needing
attention.

All filtering, sorting, pagination, and summaries must be backend-driven.

## Overall fulfillment status

A Sale is fully fulfilled when `TakenNow + Delivered + Claimed = SoldQuantity`. Pending and
Unscheduled quantities are not completed.

Derive Sale-level states equivalent to: Fulfilled; Partially fulfilled; Awaiting delivery; Awaiting
pickup; Awaiting delivery and pickup; Needs scheduling; Needs attention.

Avoid storing a duplicated Sale status unless necessary and protected against inconsistency.

## API requirements

Follow existing API conventions. Add or adapt commands and endpoints for: creating Pickup schedules;
retrieving Sale Pickup schedules; retrieving Pickup details; marking Pickup Claimed; cancelling
Pickup with PickupLater; cancelling Pickup and converting to Delivery; cancelling Delivery with
DeliverLater; cancelling Delivery and converting to Pickup; Customer picked up instead; Pickup
reports; combined fulfillment reports.

Every command must: enforce tenant isolation; enforce authorization; validate Sale and SaleItem
ownership; recalculate quantity availability on the backend; use a transaction; use the project's
concurrency mechanism; reject stale or invalid transitions with appropriate conflict responses; be
retry-safe where records are created; avoid trusting client-computed availability.

## Authorization

Use existing policies. Recommended permissions:

| Action                 | Suggested roles                              |
| ---------------------- | -------------------------------------------- |
| View fulfillment       | Roles permitted to view Sales                |
| Create Delivery/Pickup | Cashier, Manager, Admin, Owner or equivalent |
| Mark Delivered         | Authorized delivery or management roles      |
| Mark Claimed           | Cashier, Manager, Admin, Owner or equivalent |
| Cancel or convert      | Manager, Admin, Owner or equivalent          |
| View financial totals  | Roles permitted to view financial reports    |

Backend authorization is mandatory.

## Migration

Create a safe EF Core migration following the database-per-tenant process. Requirements:

- Preserve existing Sales.
- Preserve DeliveryRequiredQuantity.
- Preserve DeliveryReceipt and DeliveryReceiptItem history.
- Add Pickup-related persistence.
- Add cancellation-disposition support.
- Add audited conversion persistence or reuse an adequate existing audit system.
- Add foreign keys, unique constraints, indexes, and concurrency fields.
- Existing Sales without Pickup data default to zero Pickup allocation.
- Do not create a false Pending Pickup backlog.
- Existing Delivered and Cancelled Delivery records retain their meanings.
- Existing TakeNow quantities remain TakeNow.

Inspect generated migration SQL before applying it.

## Concurrency and atomicity

Protect these scenarios: two users allocate the same available quantity; Delivery is cancelled while
another user marks it Delivered; Pickup is cancelled while another user marks it Claimed; two users
convert the same quantities; a replacement schedule is created twice after a retry; two schedules
receive the same sequence number; a cancellation succeeds but replacement creation fails; cross-tenant
items are submitted.

Cancellation plus conversion plus replacement-record creation must succeed or fail as one transaction.

## Required tests

Add or update automated tests for at least:

1. TakeNow is the checkout default.
2. Quantities split across TakeNow, Delivery, and Pickup.
3. Allocation cannot exceed sold quantity.
4. TakeNow cannot be used as a post-sale cancellation disposition.
5. Pending Pickup can be marked Claimed.
6. Claimed Pickup is successful and terminal.
7. Marking Pickup Claimed does not cancel it.
8. Pending Pickup cancelled with PickupLater releases its quantities.
9. Pending Pickup converted to Delivery creates a new Pending Delivery.
10. Cancelled Pickup remains in history.
11. Pickup cancellation does not offer TakeNow.
12. Pending Delivery cancelled with DeliverLater releases its quantities.
13. Pending Delivery converted to future Pickup creates a Pending Pickup.
14. Customer picked up instead cancels the Delivery.
15. Customer picked up instead creates a separate Completed Pickup.
16. Customer picked up instead records ClaimedAt and ClaimedBy.
17. Customer picked up instead does not mark the Delivery Delivered.
18. Customer picked up instead does not create TakeNow fulfillment.
19. Converted source and replacement records retain audit linkage.
20. Failed replacement creation rolls back the cancellation.
21. Retried conversion does not create duplicates.
22. Delivered Delivery cannot be cancelled or converted.
23. Claimed Pickup cannot be cancelled or converted.
24. Pending and completed quantities cannot be allocated again.
25. Additional schedules require Unscheduled quantities.
26. Delivery charge remains unchanged after conversion.
27. Pickup does not add a delivery charge.
28. Multiple Delivery rows do not duplicate delivery-charge revenue.
29. Delivery reports exclude successful Pickup counts.
30. Pickup reports include Customer picked up instead as Claimed.
31. Combined fulfillment totals are correct.
32. Sale-level fulfillment status is correct.
33. Tenant isolation is enforced.
34. Unauthorized actions are rejected.
35. Concurrent actions result in only one valid transition.
36. Existing delivery behavior remains functional.
37. Existing data migrates correctly.
38. Backend tests pass.
39. Frontend tests and TypeScript checks pass.
40. Production frontend build passes.

## UI verification scenarios

Verify live:

1. Checkout a Sale containing TakeNow, Delivery, and Pickup quantities.
2. Mark a Pending Pickup Claimed and confirm it is shown as successful.
3. Cancel a Pickup using PickupLater.
4. Cancel a Pickup and convert it to Delivery.
5. Cancel a Delivery using DeliverLater.
6. Convert a Delivery to a future Pending Pickup.
7. Use Customer picked up instead and confirm: Delivery is Cancelled; Pickup is Completed/Claimed; no
   TakeNow adjustment is created.
8. Confirm reports classify each result correctly.
9. Confirm delivery-charge revenue is not duplicated.

## Implementation sequence

Work in small, reviewable stages: 1. Inspect and summarize the current implementation. 2. Choose and
explain the data-model approach. 3. Update domain models and EF Core configuration. 4. Create and
inspect the migration. 5. Implement Pickup scheduling and claiming. 6. Implement corrected
cancellation dispositions. 7. Implement atomic conversions and audit events. 8. Update POS checkout
allocation. 9. Update Sale details. 10. Add Pickup reporting. 11. Update Delivery and combined
fulfillment reports. 12. Add automated tests. 13. Run live end-to-end verification. 14. Review the
final diff for unrelated changes.

Do not merge or push.

## Required final report

- Selected architecture and reasoning
- Domain and database changes
- Migration and legacy-data handling
- API changes
- POS changes
- Pickup workflow
- Corrected cancellation behavior
- Audit and conversion implementation
- Reporting changes
- Concurrency and transaction strategy
- Files changed
- Tests run and exact results
- Live scenarios verified
- Assumptions, limitations, or unresolved issues
