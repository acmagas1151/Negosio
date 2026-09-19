# Simplify POS Fulfillment to Whole-Sale Level

_Verbatim user request, 2026-09-19. Supersedes the per-item/partial-allocation design shipped on
this same branch by the 2026-09-13 pickup-fulfillment plan (see
`docs/superpowers/specs/2026-09-13-pickup-fulfillment.md` and
`docs/superpowers/reports/2026-09-13-pickup-fulfillment-final-report.md` for what it built)._

Read `docs/handover.md`, inspect the current implementation, and review the recent
fulfillment-related commits before changing anything.

The latest partial/mixed-fulfillment interface is more complicated than intended. Simplify the POS
checkout according to the finalized requirements below.

Do not merge or push changes.

## Final approved fulfillment design

Fulfillment is now selected at the **whole-Sale level**, not per Sale item.

The Payment modal must offer exactly three mutually exclusive choices:

1. `Take now`
2. `For delivery`
3. `For pickup`

`Take now` is selected by default.

The selected method applies immediately to every item and the full quantity in the Sale.

Remove from the POS interface:

- Split order
- Mixed fulfillment
- Partial delivery
- Partial pickup
- Per-item fulfillment quantities
- Per-item Take now, Delivery, and Pickup inputs
- Deliver-later item allocation
- Pickup-later item allocation
- Multiple initial schedules during checkout
- Fulfillment allocation tables

Do not show any fulfillment controls on the main cart screen. The three choices belong only inside
the existing Take Payment/Payment modal.

## Required POS flow

The cashier flow must be:

1. Add products to the cart.
2. Click Charge.
3. Open the Take Payment modal.
4. Choose one fulfillment method:
   - Take now
   - For delivery
   - For pickup
5. Enter additional details only when required.
6. Select the payment method.
7. Confirm payment.

The common Take-now checkout must remain short and fast.

## Payment modal layout

At the top of the Payment modal, show a compact segmented selection or radio-card group:

```text
How will the customer receive the order?

[ Take now ] [ For delivery ] [ For pickup ]
```

Requirements:

- Take now is selected by default.
- The choices are mutually exclusive.
- Do not include Split order.
- Do not render a long Sale-item table.
- Do not show per-item quantity inputs.
- Keep payment methods and payment fields immediately reachable.
- Keep Cancel and Confirm Payment in a pinned/sticky footer.
- Use internal scrolling only when the selected Delivery or Pickup details require it.
- Do not open a second or nested modal.

## Take now behavior

When Take now is selected:

- Apply Take now to all Sale items and quantities.
- Do not create a Delivery record.
- Do not create a Pickup record.
- Delivery charge must be `0`.
- Do not require recipient or customer information.
- Do not show Delivery or Pickup fields.
- Allow the cashier to proceed directly to payment.

Do not add optional Sale-level customer information yet. That will be implemented as a separate
feature later.

## For delivery behavior

When For delivery is selected:

- Apply Delivery to all items and full quantities in the Sale.
- Create one Delivery record covering the complete Sale.
- Show these fields inline:
  - Scheduled delivery date
  - Recipient name
  - Delivery address
  - Contact number
  - Delivery notes
  - Delivery charge
- Scheduled delivery date defaults to the business-local current date.
- Allow today or a future date.
- Recipient name and address remain required.
- Follow existing validation for contact number and notes.
- Delivery charge may be zero for free delivery.
- Delivery charge remains stored once on the Sale and included once in the Sale total.

Do not ask the cashier to select products or quantities. Every Sale item belongs to the Delivery
record.

### Items taken immediately as an exception

If some products are taken by the customer immediately even though the Sale is marked For
delivery, the cashier records this manually in Delivery notes.

Example:

```text
Customer took 2 Sprite and 1 Coke during checkout.
Remaining items are for delivery.
```

The system does not structurally track this exception.

The Delivery Receipt still covers and lists the complete Sale. Staff must read the notes to
understand which items were taken immediately.

Display Delivery notes prominently on:

- Delivery details
- Printed Delivery Receipt
- Sale details
- Relevant Delivery report/detail views

## For pickup behavior

When For pickup is selected:

- Apply Pickup to all items and full quantities in the Sale.
- Create one Pickup record covering the complete Sale.
- Show these fields inline:
  - Expected pickup date
  - Customer/recipient name
  - Contact number
  - Pickup notes
- Do not require an address.
- Expected pickup date defaults to the business-local current date.
- Allow today or a future date.
- Customer/recipient name and contact number follow the current required-field decisions.
- Pickup does not create or add a delivery charge.
- Delivery charge must be normalized to `0` for a Pickup-only Sale.

Do not ask the cashier to select products or quantities. Every Sale item belongs to the Pickup
record.

If some products are taken immediately, the cashier records this in Pickup notes.

Example:

```text
Customer took 1 item during checkout.
Remaining items will be collected on the pickup date.
```

Display Pickup notes prominently on:

- Pickup details
- Printed Pickup document, if one exists
- Sale details
- Pickup reports

## Important reporting limitation

This simplified design intentionally treats fulfillment at the whole-Sale level.

Notes about items taken immediately are informational only.

Therefore:

- The system does not calculate exact Take-now item quantities for a Delivery or Pickup Sale.
- Delivery and Pickup reports do not derive item-level exceptions from free-text notes.
- A Delivery record continues to list all Sale items.
- A Pickup record continues to list all Sale items.
- Marking a Delivery Delivered completes the complete Delivery record.
- Marking a Pickup Claimed completes the complete Pickup record.
- Reports must not claim exact item-level partial-fulfillment accuracy.

Do not attempt to parse the notes into structured fulfillment quantities.

## Simplified relationship rules

For new Sales:

```text
Take now
→ no Delivery or Pickup record

For delivery
→ one active Delivery record covering the complete Sale

For pickup
→ one active Pickup record covering the complete Sale
```

A Sale must not have an active Delivery and an active Pickup simultaneously.

A Sale must not have multiple active Delivery schedules or multiple active Pickup schedules under
the new simplified workflow.

Cancelled historical records may remain, so a Sale can have:

- Previous Cancelled Delivery/Pickup records
- One current active replacement record

Completed records remain historical and terminal.

## Status behavior

### Delivery

Supported states:

- Pending
- Delivered
- Cancelled

Rules:

- Pending may become Delivered.
- Pending may become Cancelled.
- Delivered is terminal.
- Cancelled is terminal.
- Marking Delivered completes the whole Delivery record.

### Pickup

Supported states:

- Pending
- Claimed/Completed
- Cancelled

Rules:

- Pending may become Claimed.
- Pending may become Cancelled.
- Claimed is terminal.
- Cancelled is terminal.
- Marking Claimed completes the whole Pickup record.

Take now is a checkout fulfillment method, not a post-sale status or cancellation disposition.

## Cancellation and replacement behavior

Preserve audit history.

Do not reactivate or overwrite Cancelled records.

### Cancel Delivery

Allowed outcomes:

#### Deliver later

- Cancel the current Delivery.
- Preserve the cancelled record and reason.
- Create a new Pending Delivery covering the complete Sale.
- Require a new valid delivery date.
- Prefill recipient, address, contact, notes, and delivery charge where appropriate.

#### Convert to Pickup

- Cancel the current Delivery.
- Preserve its history.
- Create one new Pending Pickup covering the complete Sale.
- Require expected pickup date, customer name, and contact number.

#### Customer picked up instead

- Cancel the current Delivery.
- Preserve its history.
- Create one new Pickup record covering the complete Sale.
- Mark the new Pickup immediately Completed/Claimed.
- Record ClaimedAt and ClaimedBy.
- Do not mark the Delivery as Delivered.
- Do not convert it to Take now.

### Cancel Pickup

Allowed outcomes:

#### Pickup later

- Cancel the current Pickup.
- Preserve the cancelled record and reason.
- Create a new Pending Pickup covering the complete Sale.
- Require a new expected pickup date.

#### Convert to Delivery

- Cancel the current Pickup.
- Preserve its history.
- Create a new Pending Delivery covering the complete Sale.
- Require delivery date, recipient, address, and contact information.

If the customer collects a Pending Pickup, use Mark as Claimed. Do not cancel it.

All cancellation and replacement operations must be atomic and auditable.

## Delivery charge behavior

Preserve the existing completed DeliveryCharge implementation.

Rules:

- Store DeliveryCharge once on the Sale.
- Add it to the Sale total once.
- Allow zero for free delivery.
- Do not copy it into every Delivery/Pickup record.
- Take now and Pickup default to zero delivery charge.
- Multiple historical cancelled/replacement records must not multiply delivery-charge revenue.
- Cancelling or converting fulfillment must not silently refund or modify a completed Sale.
- Refunds remain a separate financial workflow.

Delivery-charge reports must aggregate by distinct Sale.

## Existing partial-fulfillment implementation

The current branch may already contain:

- `DeliveryRequiredQuantity`
- Pickup-required quantities
- DeliveryReceiptItem
- PickupItem
- Per-item allocation services
- Partial-fulfillment reports
- Migrations supporting partial quantities
- Tests for multiple schedules

Do not immediately delete these database structures or migrations.

First:

1. Inspect whether they are already referenced by persisted test/demo data.
2. Inspect whether later migrations depend on them.
3. Determine whether removing them would make the migration chain unsafe.
4. Determine whether reports, receipts, or APIs rely on them.

Preferred safe approach:

- Simplify the POS UI and business workflow first.
- For new Delivery/Pickup records, automatically include all Sale items at their complete sold
  quantities.
- Hide partial-allocation behavior from normal users.
- Preserve existing historical records.
- Keep currently required child-item tables if receipts and reports use them.
- Remove dead frontend allocation state and controls.
- Remove backend partial-allocation paths only when it is safe and clearly unused.
- Do not rewrite or delete migration history casually.

If this branch has not been deployed anywhere and the migration chain can safely be simplified,
explain the proposed cleanup before doing destructive schema removal.

## API validation

For new checkout requests:

- Fulfillment method must be exactly one of TakeNow, Delivery, or Pickup.
- Default missing fulfillment method to TakeNow only if backward compatibility requires it.
- Delivery applies to all Sale items.
- Pickup applies to all Sale items.
- Reject requests attempting mixed or partial fulfillment.
- Reject simultaneous Delivery and Pickup creation.
- Reject multiple active fulfillment records for one Sale.
- Backend must not trust only the frontend selection.

If child allocation rows are still required internally, create them automatically for every Sale
item using the full sold quantity.

## Switching selections in the Payment modal

When the cashier changes the fulfillment choice:

### Delivery → Take now

- Hide Delivery fields.
- Normalize delivery charge to `0`.
- Do not submit Delivery details.

### Pickup → Take now

- Hide Pickup fields.
- Do not submit Pickup details.

### Delivery → Pickup

- Hide Delivery-only fields.
- Normalize delivery charge to `0`.
- Show Pickup fields.
- Do not submit stale Delivery data.

### Pickup → Delivery

- Hide Pickup-only fields.
- Show Delivery fields.
- Show delivery charge.
- Do not submit stale Pickup data.

It is acceptable to preserve draft values locally while the Payment modal remains open, but only
the currently selected method may be submitted.

## Payment and retry behavior

Preserve fulfillment selection and entered details when checkout fails:

- Selected method
- Delivery/Pickup date
- Recipient/customer information
- Address
- Contact number
- Notes
- Delivery charge

Reset after:

- Successful checkout
- New Transaction
- Explicit reset

The final total, cash requirement, and change must include DeliveryCharge only when For delivery is
selected.

## Sale details and receipts

### Take-now Sale

- Show fulfillment method as Take now if useful.
- Do not show Delivery or Pickup records.

### Delivery Sale

Show:

- For delivery
- Delivery status
- Scheduled date
- Complete Sale item list
- Recipient information
- Address
- Contact
- Delivery notes
- Delivery charge
- Cancellation/replacement history

### Pickup Sale

Show:

- For pickup
- Pickup status
- Expected pickup date
- Complete Sale item list
- Customer information
- Contact
- Pickup notes
- Cancellation/replacement history

Do not show obsolete partial-quantity summaries for new whole-Sale records.

## Reports

### Delivery report

One row per Delivery record, including cancelled history:

- Related Sale #
- Delivery sequence
- Scheduled date
- Status
- Recipient
- Address
- Contact
- Notes
- Delivery charge from the Sale
- Delivered/cancelled timestamps
- Cancellation reason and disposition

Do not multiply DeliveryCharge across replacement records.

### Pickup report

One row per Pickup record:

- Related Sale #
- Pickup sequence
- Expected pickup date
- Status
- Customer name
- Contact
- Notes
- Claimed/cancelled timestamps
- Cancellation reason and disposition

### Fulfillment reporting

Simplify Sale-level fulfillment to:

- Take now
- Pending delivery
- Delivered
- Pending pickup
- Claimed
- Cancelled/replaced

Do not present item-level partial-allocation metrics for new Sales because notes are not
structured.

## UI acceptance criteria

Verify:

1. The main POS cart has no fulfillment controls.
2. Clicking Charge opens the Payment modal.
3. Payment modal shows exactly:
   - Take now
   - For delivery
   - For pickup
4. Take now is selected by default.
5. Split order is removed.
6. Partial/mixed controls are removed.
7. Take now shows no extra fields.
8. Payment methods are immediately visible for Take now.
9. For delivery expands only Delivery fields.
10. For pickup expands only Pickup fields.
11. Delivery applies to all Sale items.
12. Pickup applies to all Sale items.
13. Notes are available and printed/displayed prominently.
14. Switching methods does not submit stale data.
15. Delivery charge is applied only for Delivery.
16. Cash and change calculations remain correct.
17. Failed checkout preserves selected method and entered details.
18. Footer actions remain visible.
19. Modal fits at 1366×768 and 1920×1080.
20. No horizontal page scrolling is introduced.

## Backend acceptance criteria

Verify:

1. Take-now checkout creates no Delivery or Pickup.
2. Delivery checkout creates one Pending Delivery for the complete Sale.
3. Pickup checkout creates one Pending Pickup for the complete Sale.
4. Mixed/partial crafted requests are rejected.
5. Delivery and Pickup cannot both be active.
6. All Sale items are included automatically.
7. Delivered completes the Delivery.
8. Claimed completes the Pickup.
9. Cancelled records remain historical.
10. Replacement creation is atomic.
11. Customer picked up instead produces Cancelled Delivery plus Claimed Pickup.
12. DeliveryCharge remains stored and counted once.
13. Tenant isolation is preserved.
14. Authorization remains enforced.
15. Existing records and migrations remain readable.

## Verification

Run:

- Backend unit and integration tests
- Frontend tests
- TypeScript type checking
- Production frontend build
- Migration verification
- Live POS checkout for all three methods
- Delivery cancellation and replacement scenarios
- Pickup claiming and cancellation scenarios
- Delivery and Pickup report verification

Review the diff for unrelated changes.

Do not merge or push.

## When finished, report

- Current implementation found
- Simplification approach selected
- UI components changed
- Partial/mixed controls removed
- Backend behavior changed
- Database structures retained or removed, with reasoning
- Migration impact
- Receipt and report changes
- Tests run and exact results
- Live scenarios verified
- Remaining limitations
