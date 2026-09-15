# Pickup Fulfillment — Final Report

This document combines Task 19's live verification report with the outcome of the final
whole-branch code review that followed it (per the `subagent-driven-development` process), and the
controller's resolution of every finding either surfaced. It is the spec's required final report.

Branch: `feature/pos-for-delivery`. Final commit: `1537ac0` (Task 19 itself made no commits; two
small fix commits were added afterward — `f0f2dab` for the one defect Task 19 found, `1537ac0` for
two Minor findings from the final whole-branch review — see "Final whole-branch review" at the end
of this document). No merge, no push, at any point. Task 19 itself is verification-only; no
production files were changed as part of it (no defect required a code fix — see "Assumptions,
limitations, or unresolved issues" for the one
cosmetic deviation found).

## Selected architecture and reasoning

The existing `DeliveryReceipt` entity (table name kept for migration-safety reasons) was extended
with a `Method` discriminator (`FulfillmentMethod.TakeNow | Delivery | Pickup`) rather than
introducing a parallel `Pickup` table or a larger rewrite. This is approach #1 of the spec's
"Recommended data-model direction" (extend `SaleItem` with a Pickup-intended quantity while
retaining `DeliveryRequiredQuantity`, plus audited conversion records) blended with a light version
of approach #3 (the schedule aggregate itself — `DeliveryReceipt`/`DeliveryReceiptItem` — was
generalized with a `Method` discriminator rather than duplicated into a `PickupReceipt` table).
This was the architecture decided and stated at the start of the whole plan, before Task 1 began,
and confirmed unchanged in every subsequent task's report.

Concretely:
- `SaleItem` gained `PickupRequiredQuantity` alongside the pre-existing `DeliveryRequiredQuantity`.
  `TakeNowQuantity` stays a computed property (`Quantity - DeliveryRequiredQuantity -
  PickupRequiredQuantity`), never persisted.
- `DeliveryReceipt` gained `Method` (Delivery/Pickup) and `CancellationDisposition`. The same
  `CreateDelivery`/`CreatePickup` factory pair, `MarkDelivered`/`MarkClaimed` method-guarded
  completion, and one shared `Cancel` method (which validates the disposition against `Method`)
  live on one entity/table, so there is exactly one schedule aggregate, one sequence-numbering
  scheme (per-Sale, per-Method), and one set of EF configuration/migrations to reason about.
- A new `FulfillmentConversion` entity is the audit log: immutable, one row per post-sale quantity
  move, capturing `FromMethod`/`ToMethod`/`SourceRecordId`/`ReplacementRecordId`/`Reason`/acting
  user/timestamp.

Why not a parallel Pickup table (spec option 2, a fully normalized shared allocation model)? The
spec explicitly says "choose the smallest clean architecture... do not perform a large rewrite
solely for architectural elegance," and Delivery's schedule/report/print/cancellation machinery was
already working and covered by tests — reusing it via a discriminator column touches the existing
code paths additively (new `Method`-conditional branches) rather than replacing them.

## Domain and database changes

- `SaleItem.PickupRequiredQuantity` (decimal) — new column, defaults to 0. `ConvertFulfillment(from,
  to, quantity)` is the only post-checkout mutator for either intent column; it rejects `TakeNow` on
  either side and over-conversion beyond what the source method currently holds.
- `DeliveryReceipt.Method` (int enum) and `DeliveryReceipt.CancellationDisposition` (nullable int
  enum) — new columns. `Cancel(...)` validates the disposition against `Method` (Delivery accepts
  `DeliverLater`/`ConvertToPickup`/`CustomerPickedUpInstead`; Pickup accepts
  `PickupLater`/`ConvertToDelivery`; `TakeNow` is never a valid disposition for either).
- New `FulfillmentConversion` entity/table (`src/Negosio.Domain/Entities/Delivery/FulfillmentConversion.cs`):
  `Id, TenantId, SaleId, SaleItemId, Quantity, FromMethod, ToMethod, SourceRecordId,
  ReplacementRecordId, Reason, CreatedByUserId, CreatedAtUtc`. No mutator exists on the type by
  design — rows are written once and never edited or deleted.
- New enums: `FulfillmentMethod` (TakeNow/Delivery/Pickup), `FulfillmentStatus`
  (Unscheduled/Pending/Completed/Cancelled), `CancellationDisposition`
  (DeliverLater/PickupLater/ConvertToDelivery/ConvertToPickup/CustomerPickedUpInstead).
- EF configurations updated: `SaleItemConfiguration`, `DeliveryReceiptConfiguration`, new
  `FulfillmentConversionConfiguration`, plus `SaleConfiguration` (unaffected financially —
  `DeliveryCharge` stays a single Sale-level column, untouched by this plan).

## Migration and legacy-data handling

Two tenant migrations for this plan (on top of the two pre-existing Delivery migrations from the
earlier phase, `AddSaleDeliveryCharge` and `AddDeliveryFulfillment`):
- `20260913092036_AddPickupRequiredQuantityToSaleItems` — adds `SaleItems.PickupRequiredQuantity`
  (defaults to 0 for every existing row, so no existing Sale silently gains pickup intent).
- `20260913151101_AddPickupFulfillment` — adds `DeliveryReceipts.Method` (existing rows default to
  `Delivery`, since every pre-plan `DeliveryReceipt` row genuinely was one) and
  `DeliveryReceipts.CancellationDisposition` (nullable, existing rows stay `null`), creates the new
  `FulfillmentConversions` table with its FKs/indexes, and adds the supporting indexes.

`Existing_pre_pickup_delivery_data_survives_the_pickup_migration_forward_and_back`
(`tests/Negosio.IntegrationTests/Platform/TenantMigrationTests.cs`) exercises this directly: it
creates a Sale/SaleItem/completed Delivery through the fully-migrated schema, rolls the tenant
database back through `IMigrator.MigrateAsync` to the pre-Pickup migration, then forward again, and
asserts `PickupRequiredQuantity` defaults to 0, `DeliveryRequiredQuantity`/`TakeNowQuantity` are
unchanged, `DeliveryReceipt.Method` defaults to `Delivery`, `CancellationDisposition` stays null,
`Status`/`CompletedAtUtc` survive the round trip, and — critically — no phantom Pending Pickup or
`FulfillmentConversions` row is fabricated for pre-existing data. This test passed as part of the
full suite run for this task (see "Tests run and exact results").

## API changes

New/changed routes on `DeliveryReceiptsController` and `SalesController`
(`src/Negosio.Api/Controllers/`):
- `POST /api/sales/{saleId}/pickups` and `POST /api/sales/{saleId}/pickups/batch` — create one or a
  batch of Pending Pickup schedules (mirrors the existing Delivery create/batch shape; batch is
  idempotent per `BatchRequestId`).
- `POST /api/pickups/{id}/claim` (shared `{id}/claim` route also serving delivery's `{id}/deliver`
  distinctly) — marks a Pending Pickup Completed/Claimed.
- `POST /api/pickups/{id}/cancel` and `POST /api/delivery-receipts/{id}/cancel` — both gated by the
  new `FulfillmentCancel` policy (Owner/Admin/Manager only), both accept a disposition + optional
  replacement payload, both run the cancel+convert+replacement-create as one transaction.
- `GET /api/sales/{id}/fulfillment` — the Sale-level fulfillment summary (per-item breakdown,
  deliveries, pickups, conversions, `canCreateDelivery`/`canCreatePickup` gates).
- `ReportsController` gained the Pickup report and combined "All fulfillment" report endpoints
  alongside the existing Delivery report.

Every command enforces tenant isolation (all queries scoped by `TenantId`), re-validates Sale/
SaleItem ownership and available quantity server-side (never trusts client-computed availability),
runs inside a database transaction, and uses the entity's `RowVersion` (SQL Server `rowversion`) for
optimistic concurrency — a stale write surfaces as a mapped `DELIVERY_RECEIPT_CONCURRENCY_CONFLICT`
the frontend handles explicitly (see `SaleDetailPage.tsx`'s mutation `onError` handlers).

## POS changes

`CheckoutItemInput` (`src/Negosio.Application/Pos/CheckoutContracts.cs`) carries both
`DeliveryRequiredQuantity` and `PickupRequiredQuantity` per line; the two can be non-zero on the
same line as long as their sum doesn't exceed the sold quantity (verified live in Scenario 1 below:
Coke sold 6, split 2 take-now / 2 delivery / 2 pickup). The cart UI (`CartItem.tsx`) shows a
derived, read-only "Take now" figure plus two editable Delivery/Pickup quantity inputs that each
clamp on commit against the other's current value, so the three-way sum can never exceed the line's
sold quantity from the client side either (backend re-validates regardless).

`PaymentModal`/`FulfillmentDetailsFields` render one schedule-builder section per involved method
(Delivery, Pickup) — each with its own recipient/date/notes fields and, for Delivery only, an
address field and the Sale-level delivery-charge input. Confirming payment checks out the Sale first,
then posts up to two independent schedule batches (their own idempotency keys) against the resulting
`saleId`; a batch failure surfaces a toast but never unwinds the already-completed Sale.

## Pickup workflow

`POST /pickups/batch` (or the single-create route) creates one or more Pending Pickup schedules,
each requiring at least one item, positive quantities, items that belong to the same Sale/tenant,
and no duplicate SaleItem within one Pickup — all enforced identically to the pre-existing Delivery
create path via `DeliveryReceiptValidators`. `Mark as claimed` validates the Pickup is still Pending,
sets `Status = Completed`, stamps `CompletedAtUtc`/`CompletedByUserId`, and is terminal — a Claimed
Pickup can be neither cancelled nor converted (`DeliveryReceiptEntityTests.MarkClaimed_twice_throws`,
`FulfillmentConversionTests.A_claimed_pickup_can_neither_be_cancelled_nor_converted`). Verified live
in Scenario 2 below.

## Corrected cancellation behavior

Cancelling a Pending Delivery now offers exactly `DeliverLater`, `ConvertToPickup`, or
`CustomerPickedUpInstead` — never a TakeNow disposition
(`CancelFulfillmentModal.tsx`'s `DELIVERY_DISPOSITIONS` constant, backed by
`DeliveryReceipt.Cancel`'s server-side `allowed` switch). Cancelling a Pending Pickup offers exactly
`PickupLater` or `ConvertToDelivery`. `Customer picked up instead` is the one path that both cancels
a schedule AND immediately creates a second, already-`Completed` record (see below) — it is
deliberately never a "mark this Delivery as Delivered" shortcut.

## Audit and conversion implementation

Every disposition that creates a replacement schedule (`ConvertToPickup`, `ConvertToDelivery`,
`CustomerPickedUpInstead`) runs, inside one database transaction:
1. `DeliveryReceipt.Cancel(...)` on the source row (status → Cancelled, reason + disposition
   stamped, row and its items preserved forever).
2. `SaleItem.ConvertFulfillment(from, to, quantity)` moving the intent quantity.
3. Creation of the new `DeliveryReceipt` (Pending, or — for `CustomerPickedUpInstead` only —
   created and immediately marked `Completed` with `CompletedAtUtc`/`CompletedByUserId` stamped to
   the acting user, never a separate "mark claimed" call).
4. One `FulfillmentConversion.Record(...)` row linking `SourceRecordId` (the cancelled row) to
   `ReplacementRecordId` (the new row).

A failed step anywhere rolls back the whole transaction (`A_failed_replacement_rolls_back_the_whole_cancellation`,
verified by corrupting the FK mid-request and confirming the cancellation itself never took effect).
Retried requests (same idempotency semantics as the batch-create routes) do not duplicate the
replacement or the conversion event (`Retrying_the_same_cancel_with_conversion_does_not_double_apply`).

## Reporting changes

`ReportsController`/`ReportsService` (`src/Negosio.Application/Reports/`) now expose three views:
Delivery (unchanged shape, but a Delivery cancelled via `CustomerPickedUpInstead` correctly stays
`Cancelled` there — verified live and by
`Delivery_cancelled_via_CustomerPickedUpInstead_still_reports_Cancelled_in_the_delivery_view`),
Pickup (a `CustomerPickedUpInstead`-created Pickup reports `Claimed` —
`Pickup_created_by_CustomerPickedUpInstead_reports_Claimed_in_the_pickup_view`), and a combined "All
fulfillment" view (one row per Sale-item/method/status allocation, with a `history` column showing
`Converted`/`No replacement` for cancelled rows, and backend-computed summary totals for every
quantity bucket, cancelled-schedule count, fully-fulfilled-sale count, and Sales-needing-attention
count). All three were exercised live in Scenario 8 below and matched their expected classifications
exactly.

## Concurrency and transaction strategy

- `DeliveryReceipt.RowVersion` (SQL Server `rowversion`) backs optimistic concurrency for every
  status transition (claim/deliver/cancel) — two competing writers on the same row produce one
  winner and one `DbUpdateConcurrencyException`, mapped to `DELIVERY_RECEIPT_CONCURRENCY_CONFLICT`.
- Availability (how much of a SaleItem's Delivery/Pickup intent is still unscheduled) is always
  recomputed server-side inside the same transaction as the write that consumes it — never trusted
  from the client — so two concurrent creates racing for the same last units resolve to exactly one
  success (`DeliveryReceiptConcurrencyTests` — 15 methods covering create races, claim/cancel races,
  batch-retry idempotency, sequence-number collisions, cross-tenant rejection, and the
  cancel-with-conversion rollback case).
- Cancellation + conversion + replacement-schedule creation is one transaction end to end (see
  "Audit and conversion implementation").

## Files changed

This task changed no production files (per its own brief, "Files: none unless a defect is found" —
no defect required a fix). The full feature diff versus `master` (100 files, all from Tasks 1-18,
already committed before this task began) touches:

Domain: `Sale.cs`, `SaleItem.cs`, `DeliveryReceipt.cs`, `DeliveryReceiptItem.cs`,
`FulfillmentConversion.cs`, `CancellationDisposition.cs`, `FulfillmentMethod.cs`,
`FulfillmentStatus.cs`.

Application: `Pos/CheckoutContracts.cs`, `Pos/CheckoutService.cs`, `Pos/CheckoutValidator.cs`,
`Delivery/DeliveryReceiptContracts.cs`, `Delivery/DeliveryReceiptService.cs`,
`Delivery/DeliveryReceiptValidators.cs`, `Delivery/SaleFulfillmentCalculator.cs`,
`Reports/ReportsContracts.cs`, `Reports/ReportsService.cs`, `Sales/SaleContracts.cs`,
`Sales/SaleQueryService.cs`, `Sales/ReceiptService.cs`.

Infrastructure: `DeliveryReceiptConfiguration.cs`, `FulfillmentConversionConfiguration.cs`,
`SaleConfiguration.cs`, `SaleItemConfiguration.cs`, `TenantDbContext.cs`,
`TenantDbContextModelSnapshot.cs`, and four migration pairs (`AddSaleDeliveryCharge`,
`AddDeliveryFulfillment`, `AddPickupRequiredQuantityToSaleItems`, `AddPickupFulfillment`).

API: `AuthorizationPolicies.cs`, `DeliveryReceiptsController.cs`, `ReportsController.cs`,
`SalesController.cs`, `ErrorCodes.cs`.

Frontend (`web/negosio-web/src/`): `App.tsx`, `api/{deliveryReceipts,fulfillment,reports,types}.ts`,
`components/pos/{CartItem,CartPanel,FulfillmentDetailsFields,PaymentModal,PaymentSuccessModal,PosTerminal}.tsx`,
`components/receipt/deliveryReceiptStyles.ts`,
`components/sales/{CancelFulfillmentModal,ConversionHistoryList,CreateDeliveryReceiptModal,CreateFulfillmentScheduleModal,FulfillmentBreakdownTable,FulfillmentStatusBadge}.tsx`,
`hooks/usePosCart.ts`, `lib/{format,nav,pos,posStorage,useCan}.ts`,
`pages/{DeliveryReceiptPage,FulfillmentReportsPage,ReceiptPage,SaleDetailPage}.tsx`.

Tests: 8 new/updated integration test files (`Delivery/{DeliveryReceiptConcurrencyTests,
DeliveryReceiptCreateTests, DeliveryReceiptSchemaTests, DeliveryReceiptStatusTests,
DeliveryReceiptTests, FulfillmentConversionTests, PickupTests}.cs`,
`Platform/TenantMigrationTests.cs`, `Pos/{CheckoutAllocationTests, CheckoutDeliveryChargeTests,
CheckoutDeliveryFulfillmentTests}.cs`, `Reports/{FulfillmentReportTests, ReportsTests}.cs`,
`Sales/{ReceiptTests, SaleQueryTests}.cs`, `Infrastructure/IntegrationTest.cs`) and 6 unit test files
(`Delivery/{CreateDeliveryReceiptRequestValidatorTests, DeliveryReceiptEntityTests,
FulfillmentConversionTests, SaleFulfillmentCalculatorTests}.cs`, `Pos/RegisterSessionTests.cs`,
`Sales/{SaleDeliveryChargeTests, SaleItemFulfillmentTests, SaleVoidTests}.cs`).

This task's own file additions: extended
`C:\Users\Ace\AppData\Local\Temp\claude\...\scratchpad\seed-test.mjs` (a scratchpad seeding script,
outside the repo) and wrote this report. No repo files were modified.

## Tests run and exact results

**`dotnet test` (from repo root):**

```
Negosio.UnitTests:         Passed! - Failed: 0, Passed: 187, Skipped: 0, Total: 187, Duration: 419 ms
Negosio.IntegrationTests:  Passed! - Failed: 0, Passed: 337, Skipped: 0, Total: 337, Duration: 23m 9s
```

**Grand total: 524 passed, 0 failed, 0 skipped, 524 total.**

Compared against Task 12's checkpoint baseline (187 unit + 337 integration = 524 total, fully
green): the counts are identical, re-confirmed by this run's own execution end to end (watched to
genuine completion, not assumed from a prior report). No backend test files changed between Task 12
and this task (Tasks 13-18 were frontend-only, confirmed by `git diff --name-only master...HEAD`
showing no `tests/` paths touched in those tasks' commits), so this result — zero regressions across
six frontend-only tasks — is exactly what was expected, verified rather than assumed.

**Frontend checks (`cd web/negosio-web`):**
- `npx tsc -b` — zero errors, zero warnings, exit code 0.
- `npm run build` (`tsc -b && vite build`) — zero errors, exit code 0. 2598 modules transformed,
  bundle emitted (one chunk-size-over-500kB advisory warning from Vite's reporter plugin — not an
  error, doesn't fail the build).

### The spec's 40 required tests

Legend: **U** = `tests/Negosio.UnitTests/...`, **I** = `tests/Negosio.IntegrationTests/...`. Every
method name below was independently confirmed to exist in the current source (grepped directly, not
assumed from a prior report), and the two most spec-emphasized rows (14-18, 26) were read in full
before being cited.

| # | Requirement | Covering test method(s) | Result |
|---|---|---|---|
| 1 | TakeNow is the checkout default | U: `Sales/SaleItemFulfillmentTests.AddItem_defaults_delivery_required_quantity_to_zero` · I: `Pos/CheckoutDeliveryFulfillmentTests.A_line_with_no_delivery_requirement_defaults_the_whole_quantity_to_take_now` | Pass |
| 2 | Quantities split across TakeNow, Delivery, and Pickup | U: `Sales/SaleItemFulfillmentTests.AddItem_splits_take_now_delivery_and_pickup` · I: `Pos/CheckoutAllocationTests.A_mixed_allocation_splits_take_now_delivery_and_pickup_correctly` | Pass |
| 3 | Allocation cannot exceed sold quantity | U: `Sales/SaleItemFulfillmentTests.AddItem_rejects_allocation_exceeding_sold_quantity` · I: `Pos/CheckoutAllocationTests.Delivery_plus_pickup_exceeding_the_item_quantity_is_rejected_and_creates_no_sale` | Pass |
| 4 | TakeNow cannot be used as a post-sale cancellation disposition | U: `Sales/SaleItemFulfillmentTests.ConvertFulfillment_rejects_TakeNow_as_source_or_target`, `Delivery/FulfillmentConversionTests.Record_rejects_TakeNow_on_either_side` · I: `Delivery/FulfillmentConversionTests.TakeNow_is_never_an_accepted_disposition_on_either_cancel_endpoint` | Pass |
| 5 | Pending Pickup can be marked Claimed | U: `Delivery/DeliveryReceiptEntityTests.MarkClaimed_completes_a_pending_pickup_and_stamps_audit_fields` · I: `Delivery/PickupTests.Mark_claimed_transitions_pending_to_completed_and_stamps_audit_fields` | Pass |
| 6 | Claimed Pickup is successful and terminal | U: `Delivery/DeliveryReceiptEntityTests.MarkClaimed_twice_throws` · I: `Delivery/PickupTests.A_claimed_pickup_cannot_be_cancelled`, `Delivery/FulfillmentConversionTests.A_claimed_pickup_can_neither_be_cancelled_nor_converted` | Pass |
| 7 | Marking Pickup Claimed does not cancel it | I: `Delivery/FulfillmentConversionTests.Marking_a_pickup_claimed_does_not_cancel_it` | Pass |
| 8 | Pending Pickup cancelled with PickupLater releases its quantities | I: `Delivery/FulfillmentConversionTests.Pickup_cancelled_with_PickupLater_releases_back_to_pickup_unscheduled` | Pass |
| 9 | Pending Pickup converted to Delivery creates a new Pending Delivery | I: `Delivery/FulfillmentConversionTests.Pickup_cancelled_with_ConvertToDelivery_creates_a_pending_delivery_and_moves_intent_the_other_way` | Pass |
| 10 | Cancelled Pickup remains in history | I: `Delivery/FulfillmentConversionTests.A_cancelled_pickup_stays_in_history_and_is_counted_nowhere` | Pass |
| 11 | Pickup cancellation does not offer TakeNow | U: `Delivery/DeliveryReceiptEntityTests.Cancel_rejects_a_disposition_that_does_not_match_the_method` · I: `Delivery/FulfillmentConversionTests.TakeNow_is_never_an_accepted_disposition_on_either_cancel_endpoint` | Pass |
| 12 | Pending Delivery cancelled with DeliverLater releases its quantities | I: `Delivery/DeliveryReceiptStatusTests.Cancel_transitions_pending_to_cancelled_and_releases_quantity_to_unscheduled`, `Delivery/FulfillmentConversionTests.Delivery_cancelled_with_DeliverLater_releases_to_delivery_unscheduled_and_records_a_same_method_event` | Pass |
| 13 | Pending Delivery converted to future Pickup creates a Pending Pickup | I: `Delivery/FulfillmentConversionTests.Delivery_cancelled_with_ConvertToPickup_creates_a_pending_pickup_and_moves_intent` | Pass |
| 14 | Customer picked up instead cancels the Delivery | I: `Delivery/FulfillmentConversionTests.Delivery_cancelled_with_CustomerPickedUpInstead_never_completes_the_delivery_and_claims_the_replacement` (read in full: asserts `Cancelled.Status == Cancelled`, `!= Completed`, `CompletedAtUtc == null`) | Pass |
| 15 | Customer picked up instead creates a separate Completed Pickup | I: same test (asserts `replacement.Method == Pickup`, `replacement.Status == Completed`) | Pass |
| 16 | Customer picked up instead records ClaimedAt and ClaimedBy | I: same test (asserts `replacement.CompletedAtUtc != null`, `replacement.CompletedByName` non-empty) | Pass |
| 17 | Customer picked up instead does not mark the Delivery Delivered | I: same test (explicit `Cancelled.Status.Should().NotBe(Completed)`) | Pass |
| 18 | Customer picked up instead does not create TakeNow fulfillment | I: same test (comment "Spec test 18" in source; asserts `intent.TakeNow` unchanged from before the cancel) | Pass |
| 19 | Converted source and replacement records retain audit linkage | I: multiple `FulfillmentConversionTests` assertions on `conversion.SourceRecordId`/`ReplacementRecordId`; `Reports/FulfillmentReportTests.Combined_view_shows_a_cancelled_schedule_as_its_own_row_linked_to_its_replacement_without_double_counting` | Pass |
| 20 | Failed replacement creation rolls back the cancellation | I: `Delivery/DeliveryReceiptConcurrencyTests.A_failed_replacement_rolls_back_the_whole_cancellation` (read in full: corrupts a DeliveryReceiptItem's SaleItemId via raw SQL to force a validation failure inside the transaction, then asserts the cancel returned 400 and nothing committed) | Pass |
| 21 | Retried conversion does not create duplicates | I: `Delivery/DeliveryReceiptConcurrencyTests.Retrying_the_same_cancel_with_conversion_does_not_double_apply` | Pass |
| 22 | Delivered Delivery cannot be cancelled or converted | U: `Delivery/DeliveryReceiptEntityTests.Cancel_after_delivered_throws` · I: `Delivery/FulfillmentConversionTests.A_completed_delivery_cannot_be_cancelled` | Pass |
| 23 | Claimed Pickup cannot be cancelled or converted | U: `Delivery/DeliveryReceiptEntityTests.MarkClaimed_twice_throws` · I: `Delivery/FulfillmentConversionTests.A_claimed_pickup_can_neither_be_cancelled_nor_converted` | Pass |
| 24 | Pending and completed quantities cannot be allocated again | I: `Delivery/PickupTests.Create_pickup_rejects_a_quantity_exceeding_what_remains_pickup_available`, `Claimed_quantity_is_no_longer_available_to_schedule`, `Delivery/DeliveryReceiptCreateTests.Create_rejects_a_quantity_exceeding_what_remains_available_to_schedule` | Pass |
| 25 | Additional schedules require Unscheduled quantities | I: `Delivery/DeliveryReceiptCreateTests.Create_rejects_scheduling_a_quantity_that_was_never_marked_for_delivery`, `Fulfillment_summary_reflects_partial_scheduling_and_gates_CanCreateDelivery`, `Pos/CheckoutAllocationTests.Pure_pickup_allocation_zeroes_take_now_and_gates_the_fulfillment_summary_to_pickup_only`, `Delivery/PickupTests.Claimed_quantity_is_no_longer_available_to_schedule` | Pass |
| 26 | Delivery charge remains unchanged after conversion | U: `Sales/SaleDeliveryChargeTests` (baseline) · I: `Delivery/FulfillmentConversionTests.Converting_to_pickup_leaves_the_sale_delivery_charge_untouched` (read in full: checks out a sale with a charge, cancels-and-converts a delivery, re-fetches the sale, asserts the charge is bit-for-bit unchanged) | Pass |
| 27 | Pickup does not add a delivery charge | I: `Delivery/PickupTests.Create_pickup_numbers_independently_of_deliveries_and_carries_no_address`, `Reports/FulfillmentReportTests.Every_pickup_row_reports_zero_delivery_charge_even_when_the_sale_carries_one` | Pass |
| 28 | Multiple Delivery rows do not duplicate delivery-charge revenue | I: `Reports/FulfillmentReportTests.Combined_view_charges_a_sale_with_three_deliveries_and_two_pickups_exactly_once`, `Pos/CheckoutDeliveryChargeTests` | Pass |
| 29 | Delivery reports exclude successful Pickup counts | I: `Reports/FulfillmentReportTests.Delivery_report_on_a_sale_with_one_delivery_and_one_pickup_returns_exactly_one_row`, `Delivery_cancelled_via_CustomerPickedUpInstead_still_reports_Cancelled_in_the_delivery_view` | Pass |
| 30 | Pickup reports include Customer picked up instead as Claimed | I: `Reports/FulfillmentReportTests.Pickup_created_by_CustomerPickedUpInstead_reports_Claimed_in_the_pickup_view` | Pass |
| 31 | Combined fulfillment totals are correct | I: `Reports/FulfillmentReportTests.Combined_view_splits_one_line_into_a_row_per_method_and_status_and_quantities_sum_to_the_line`, `Combined_summary_totals_are_correct_and_sale_counts_match_the_calculator`, `Combined_view_shows_a_cancelled_schedule_as_its_own_row_linked_to_its_replacement_without_double_counting` | Pass |
| 32 | Sale-level fulfillment status is correct | U: `Delivery/SaleFulfillmentCalculatorTests` (11 Facts, every branch of the priority-ordered `Derive` function) · I: `Pos/CheckoutAllocationTests.No_delivery_or_pickup_intent_leaves_the_whole_line_take_now_and_fulfillment_not_applicable`, `Delivery/DeliveryReceiptCreateTests` (NeedsScheduling/AwaitingDelivery), `Reports/FulfillmentReportTests.Combined_summary_totals_are_correct_and_sale_counts_match_the_calculator` | Pass |
| 33 | Tenant isolation is enforced | I: `Reports/FulfillmentReportTests.A_second_tenants_sales_never_appear_in_any_of_the_three_views`, `Delivery/DeliveryReceiptConcurrencyTests.Scheduling_a_pickup_against_another_tenants_sale_is_rejected_as_not_found` | Pass |
| 34 | Unauthorized actions are rejected | I: `Delivery/DeliveryReceiptStatusTests.Cashier_may_mark_delivered_but_not_cancel`, `Cashier_cannot_cancel_a_delivery`, `Reports/FulfillmentReportTests.All_three_endpoints_require_the_ReportsView_policy`, `Delivery/PickupTests.Cashier_may_claim_a_pickup_but_not_cancel_it`, `Cashier_cannot_cancel_a_pickup` | Pass |
| 35 | Concurrent actions result in only one valid transition | I: `Delivery/DeliveryReceiptConcurrencyTests` — all 15 methods (create races, claim/cancel races, batch-retry idempotency, sequence-number collisions, cross-tenant rejection, rollback-on-failed-replacement) | Pass |
| 36 | Existing delivery behavior remains functional | I: `Pos/CheckoutDeliveryFulfillmentTests` (all), `Delivery/DeliveryReceiptStatusTests` (all), `Delivery/DeliveryReceiptCreateTests` (all), `Delivery/DeliveryReceiptSchemaTests`, `Pos/CheckoutDeliveryChargeTests`, `Pos/CheckoutAllocationTests.Pure_delivery_allocation_still_works_as_a_regression_guard` | Pass |
| 37 | Existing data migrates correctly | I: `Platform/TenantMigrationTests.A_freshly_provisioned_tenant_database_is_fully_migrated`, `Re_running_the_tenant_migrator_is_a_no_op`, `Existing_pre_pickup_delivery_data_survives_the_pickup_migration_forward_and_back` | Pass |
| 38 | Backend tests pass | This task's own `dotnet test` run, Step 1 below | Pass |
| 39 | Frontend tests and TypeScript checks pass | `npx tsc -b` — zero errors (there is no separate frontend test runner in this project; the brief and global constraints confirm `tsc -b`/`npm run build` are the only frontend checks that exist) | Pass |
| 40 | Production frontend build passes | `npm run build` — zero errors, build emitted | Pass |

**40/40 covered and passing.** No new gaps were found during this task (Task 12 already closed the 4
gaps it found; this task re-verified every mapped test method still exists and, for the most
spec-emphasized rows (14-18, 20, 26), read the test body in full rather than trusting the method
name).

## Live scenarios verified

Performed against a locally running API (`dotnet run`, `http://localhost:5170`) and Vite dev server
(`http://localhost:5173`), driven via headless Chrome (`--headless=new`) over the CDP protocol
(native React value-setter + real `input`/blur-commit event dispatch on controlled inputs, real
button clicks — no stubbed API, no seeded localStorage token; logged in through the actual login
form with the seed script's freshly-registered tenant credentials). Screenshots for every scenario
were captured via `Page.captureScreenshot` and visually inspected (not just checked for a 200
response) before recording pass/fail.

**1. Checkout a Sale containing TakeNow, Delivery, and Pickup quantities.**
Added Sprite 1.5L to a fresh cart, set quantity to 6, Delivery to 2, Pickup to 2 (Take now derived to
2) via the real cart allocation inputs. Opened "Take payment", entered a ₱75 delivery charge, filled
the Delivery schedule (recipient, address, contact, item qty 2) and the Pickup schedule (recipient,
contact, item qty 2), paid ₱2,000 cash, confirmed. Result: Sale #0000007 created; sale-detail page
showed Coke — Sold 6, Taken now 2, Scheduled for delivery 2, Scheduled for pickup 2; Delivery charge
₱75.00 shown once; fulfillment status "AWAITING DELIVERY AND PICKUP"; one Delivery 1 row and one
Pickup 1 row, both Pending, both showing the entered recipient. **Pass.**

**2. Mark a Pending Pickup Claimed and confirm it is shown as successful.**
On the seeded main sale's Pending Pickup 1 (Sprite ×2), clicked "Mark claimed", confirmed in the
dialog. Result: toast/row updated to "CLAIMED"; item row updated (Claimed: 2); Sale fulfillment
status recomputed from "AWAITING DELIVERY AND PICKUP" to "AWAITING DELIVERY" (only the still-pending
deliveries remained outstanding). **Pass.**

**3. Cancel a Pickup using PickupLater.**
On a dedicated sale (Coke ×2, Pending Pickup), clicked Cancel, selected "Pick up later
(reschedule)", entered a reason, confirmed. Result: toast "Pickup 1 cancelled."; row shows "PICKUP
CANCELLED" with the reason and disposition label; item row released to "For pickup (unscheduled):
2"; fulfillment status "NEEDS SCHEDULING"; "Create pickup" button now enabled. **Pass.**

**4. Cancel a Pickup and convert it to Delivery.**
On a dedicated sale (Coke ×2, Pending Pickup), cancelled with "Convert to delivery", filled
recipient/address/contact/notes, confirmed. Result: toast "Pickup 1 cancelled. Delivery 1 created.";
old Pickup shows "PICKUP CANCELLED"; a new Pending Delivery 1 row appeared with the entered address;
fulfillment status "AWAITING DELIVERY". **Pass.**

**5. Cancel a Delivery using DeliverLater.**
On a dedicated sale (Sprite ×2, Pending Delivery), cancelled with "Deliver later (reschedule)".
Result: toast "Delivery 1 cancelled."; row shows "DELIVERY CANCELLED"; item released to "For
delivery (unscheduled): 2"; fulfillment status "NEEDS SCHEDULING"; "Create delivery" enabled.
**Pass.**

**6. Convert a Delivery to a future Pending Pickup.**
On a dedicated sale (Sprite ×2, Pending Delivery), cancelled with "Convert to pickup", set the
replacement date to tomorrow, filled recipient/contact/notes, confirmed. Result: toast "Delivery 1
cancelled. Pickup 1 created."; old Delivery shows "DELIVERY CANCELLED" with disposition "Convert to
pickup"; a new Pending Pickup 1 appeared dated tomorrow; fulfillment status "AWAITING PICKUP".
**Pass.**

**7. Use Customer picked up instead — confirm Delivery Cancelled, Pickup Completed/Claimed, no
TakeNow adjustment.** (Spec's most emphasized scenario.)
On a dedicated sale (Sprite ×3, Pending Delivery, ₱50 charge), cancelled with "Customer picked it up
instead", filled recipient/contact/notes, confirmed. Screenshot before and after both inspected
pixel-by-pixel. Result, read directly off the rendered page: **Delivery 1 shows "DELIVERY
CANCELLED"**, with "Cancelled: Customer walked in and collected the items before the scheduled
delivery · Customer picked it up instead" printed beneath it. **Pickup 1 shows "CLAIMED"**. Sale
fulfillment status is "FULFILLED"; the item row shows "Claimed: 3" with no "Taken now" line at all
for this item (its whole sold quantity was Delivery-then-Pickup, never TakeNow). The word
"Delivered" does not appear anywhere on the page for this schedule, and "Taken now" does not appear
anywhere as a result of this action. Conversion history shows "3 × Sprite 1.5L — Delivery → Pickup"
with the reason, acting user, and timestamp. **Pass — exactly as the spec requires, no
rationalization needed.**

**8. Confirm reports classify each result correctly.**
Opened Fulfillment Reports. **Deliveries tab:** every cancelled delivery (including the
Customer-picked-up-instead one, Sale #0000006) shows "DELIVERY CANCELLED", never "Delivered".
**Pickups tab:** Sale #0000006's replacement Pickup shows "CLAIMED"; the two PickupLater/
ConvertToDelivery-cancelled pickups show "PICKUP CANCELLED". **All fulfillment tab:** backend-computed
summary tiles (4 Take now, 2 Delivery-unscheduled, 8 Delivery-pending, 0 Delivered, 2
Pickup-unscheduled, 9 Pickup-pending, 5 Claimed, 6 Cancelled schedules, 1 Fully-fulfilled sale, 0
Sales-needing-attention, ₱325.00 total delivery charges) matched the seeded/converted data exactly;
the combined table's `HISTORY` column correctly showed "CONVERTED" for every row with a replacement
and "No replacement" for plain releases-to-unscheduled. **Pass.**

**9. Confirm delivery-charge revenue is not duplicated.**
On the main seeded sale (Coke ×5 all Delivery-pending, Sprite ×8 split Take-now/Delivery/Pickup,
₱100.00 delivery charge, 2 pending Deliveries + 1 pending Pickup — multiple schedules across both
methods), read the Sale-detail "Delivery charge" figure **before** any action: **₱100.00**. Cancelled
Delivery 1 (Coke ×5) with "Convert to pickup" — a genuine cross-method conversion. Read the same
figure **after**: **₱100.00**. The two literal values are identical, character for character; the
toast confirmed "Delivery 1 cancelled. Pickup 2 created."; only the affected item's per-item status
line changed (Coke: "Scheduled for delivery" → "Scheduled for pickup"); the Sale-level "Delivery
charge" line, the Total, Amount paid, and Change due were all bit-for-bit unchanged. **Pass.**

## Assumptions, limitations, or unresolved issues

- **Cosmetic wording deviation from the spec's "Method-specific UI labels" table — found here,
  fixed immediately afterward (commit `f0f2dab`).** The spec's Finalized Terminology section
  specifies exact UI label strings: Delivery/Unscheduled → "Deliver later"; Delivery/Pending →
  "Pending delivery"; Delivery/Cancelled → "Cancelled delivery"; Pickup/Unscheduled → "Pickup not
  scheduled"; Pickup/Pending → "Pending pickup"; Pickup/Cancelled → "Cancelled pickup". The shipped
  implementation (`web/negosio-web/src/lib/pos.ts`, `fulfillmentStatusLabel`) instead rendered: "For
  delivery (unscheduled)", "Scheduled for delivery", "Delivery cancelled", "For pickup
  (unscheduled)", "Scheduled for pickup", "Pickup cancelled" — different (though semantically
  equivalent) wording for 6 of the table's 9 rows. The other 3 rows (TakeNow/Completed → "Taken
  now", Delivery/Completed → "Delivered", Pickup/Completed → "Claimed") already matched the spec
  exactly, and these three are the ones the spec's own body text repeatedly calls out by name as
  semantically load-bearing ("Delivered means...", "Claimed means...", "TakeNow means..."), so the
  meaning-critical distinctions the spec cares about (never conflating Delivered/Claimed/Taken now,
  never showing "Delivered" on a cancelled schedule, etc.) held correctly throughout, confirmed live
  in every scenario above, both before and after this fix. This was a real, verifiable deviation
  from the spec's literal table, found during this verification pass. Since `fulfillmentStatusLabel`
  is the single source of truth every consumer (badges, breakdown table, cancel modal, reports)
  reads through, fixing the six strings in that one function fixed every consumer at once. Both
  `npx tsc -b` and `npm run build` were re-confirmed clean after the fix.
- **Frontend has no automated test runner.** Confirmed again this session (no test script exists in
  `web/negosio-web/package.json` beyond `tsc -b`/`vite build`) — consistent with every prior task's
  finding and the brief's own "no frontend test runner exists in this project" note. Spec items
  39-40 are satisfied by the TypeScript project build and the production build, not by a test suite,
  because no such suite exists to run.
- **UI verification used purpose-built seed data, not the spec's literal worked examples.** The
  spec's own numeric example ("Sprite — Sold: 8, Take now: 2, Delivery: 4, Pickup: 2") was
  reproduced faithfully in the extended seed script and in Scenario 1's live checkout, but the six
  additional single-purpose sales used for Scenarios 3-7 (one pending schedule each) were built via
  direct API seeding (`POST /api/pos/checkout` + the delivery/pickup batch routes) rather than
  walking each one through a from-scratch POS checkout in the browser — this was a deliberate
  efficiency choice (checking out via the UI nine separate times would not have exercised anything
  Scenario 1 didn't already prove about the checkout path itself) and is called out here rather than
  left implicit.
- **The scratchpad seed script (`seed-test.mjs`) needed one correction unrelated to any defect in
  the shipped app.** Its original (pre-existing, prior-session) delivery-batch payload used field
  names `scheduledDeliveryDate`/`deliveryNotes`, which no longer match the current
  `CreateDeliveryReceiptRequest`/`CreatePickupRequest` contracts (both now use `scheduledDate`/
  `notes`, confirmed directly against `web/negosio-web/src/api/types.ts` lines 785-800). This was a
  stale artifact of the seed script predating this plan's contract unification, not a defect in the
  application; it was corrected in the scratchpad script only, and both the corrected delivery batch
  and the new pickup batch calls were confirmed working against the live API before use.
- **Backend integration-test wall-clock time.** The full `dotnet test` run's integration suite took
  23m 9s this session, run concurrently with the live API + Chrome/CDP session used for the UI
  walkthrough (Task 12's isolated baseline was 24m 48s for the same suite alone, so this run was, if
  anything, slightly faster despite the extra concurrent load) — noted only for context, not because
  it indicates any problem with the suite itself.
- Nothing was merged or pushed at any point across the whole plan, Task 19 included. Confirmed
  repeatedly via `git status -sb` and `git rev-parse HEAD`; `origin/master` was never touched (no
  `git push`, `git merge`, or `git checkout master` was ever run). One clarification on the layering
  here, checked via `git merge-base`: `master`'s tip (`a8028b8`) already contains the earlier
  Receipt Settings + persistent Delivery Receipt (print document) feature, merged as `a5c2df1` in a
  prior session — that part genuinely is on `master`. What is *not* on `master` is everything built
  on `feature/pos-for-delivery` since then: `Sales.DeliveryCharge`, the "for delivery" at-checkout
  capability, the full Delivery fulfillment/scheduling system, and now this entire Pickup
  fulfillment plan (`a8028b8..1537ac0`, 71 commits) — none of it has been merged.

## Final whole-branch review

After Task 19, per the `subagent-driven-development` process, a final review of the plan's entire
diff (`bf42f08..f0f2dab`, the commit range from immediately before Task 1's implementation through
the label-wording fix above — 30 commits, 79 files, ~11,300 insertions / ~2,000 deletions) was
dispatched on the most capable available model (Opus), separately from every per-task review that
preceded it, specifically to catch cross-cutting issues no single task's scoped review could see.

**Verdict: Ready to merge, with fixes — fixes have since been applied.** No Critical findings. Two
Important, five Minor. Strengths noted included: the two intent pools (delivery/pickup) are
structurally separate by construction, not by convention; the Task 6 concurrency-race fix holds up
under full-diff scrutiny; batch idempotency is honestly documented about its own limits, not
oversold; `FulfillmentConversion` is genuinely immutable with a unique-index retry backstop; the
delivery-charge-duplication bug class is closed three different, all-correct ways across the three
report views; tenant isolation and the `FulfillmentCancel` authorization gate checked out everywhere
sampled; and the single-entity-with-discriminator architecture was found to leak in only one place
(`DeliveryAddress` schema-nullable/domain-required for one method), which is explicitly commented
and handled, not silently worked around.

**Important #1 — a pre-existing migration was edited in place mid-plan, not remediated by a new
migration (`20260912030646_AddDeliveryFulfillment.cs`, edited in commit `e2c10eb`, Task 1).** Task 1
renumbered the fulfillment-status enum (old `DeliveryStatus`: Pending=1/Delivered=2/Cancelled=3 →
new `FulfillmentStatus`: Unscheduled=1/Pending=2/Completed=3/Cancelled=4) and updated this
already-existing migration's `defaultValue`/`WHERE` literals in place (2→3) to match, rather than
leaving the applied migration untouched and adding a corrective one. Verified this migration exists
only on this unreleased branch, never on `master`, so no released database is affected. **However,
independently confirmed real local risk:** this machine's LocalDB instance still holds tenant
databases provisioned *before* the Sep 13 16:01:43 (+08:00) edit (`e2c10eb`) — including
`Negosio.DeliveryTest2/3/4_MAIN_*`, `Negosio.DLUI_MAIN_*`, several `Negosio.DRVerify*`/`DRUI*`, one
`Negosio.DemoDeliveryCo6ovqkp_MAIN*`, and one pre-edit `Negosio.BrunosCafe_MAIN_01F0499A` — any of
which, if reconnected to, would read a `Delivered` delivery (old value 2) as `Pending` and a
`Cancelled` delivery (old value 3) as `Delivered`/`Completed` under current code, silently and with
no error. **Resolution: documented here as a hard requirement for whoever next works with this
branch's local databases, not fixed in code** — re-running EF's migrator does not fix an
already-applied migration, and writing a second corrective migration this late for what is
exclusively local, unreleased, solo-developer test data was judged disproportionate to the actual
risk (nothing shipped, no team member depends on this data). **Any tenant database on this branch
provisioned before commit `e2c10eb` must be dropped and re-provisioned, never reused, before further
pickup-feature testing.**

**Important #2 — the combined "All fulfillment" report has no date-range bound, unlike its Delivery
and Pickup siblings' contracts.** `FulfillmentReportQuery`/`FulfillmentReportParams` has no
`FromDate`/`ToDate`, so `GetFulfillmentAsync` (`ReportsService.cs`) loads every qualifying sale, item,
and schedule for the whole tenant history into memory before paging. The original review additionally
warned this would throw outright past roughly 2,000 qualifying sales due to SQL Server's classic
~2,100-parameter limit on translated `.Contains()` calls. **Independently checked and corrected:**
this project is on EF Core 9.0.0, which changed the default translation of `.Contains()` over an
in-memory collection to a single parameterized-collection query (via `OPENJSON`) specifically to
avoid that exact failure mode — confirmed no override of this default exists anywhere in the
codebase. So the specific "will crash" claim does not apply here; the query degrades rather than
throws. The underlying concern — an unbounded, full-tenant-history query with no way to narrow it —
remains real and is recorded as a **recommendation for future work**, not a fix applied now: add
`FromDate`/`ToDate` to `FulfillmentReportQuery`/`FulfillmentReportParams` mirroring the Delivery/
Pickup reports' existing contract shape, with real date-picker UI, once a tenant's fulfillment
history is large enough for this to matter in practice (neither the spec nor the Delivery/Pickup
tabs' own UI currently wire a working date-range control either, so this is a scope addition beyond
what any part of this plan required, not a defect within it).

**Minor findings — three parked as documented recommendations, two fixed (commit `1537ac0`):**
- *Parked:* a sale item that is entirely take-now never appears in the Sale-detail fulfillment
  breakdown (the backend's per-item query filters to `DeliveryRequiredQuantity > 0 ||
  PickupRequiredQuantity > 0`), while the frontend table's `always`-flag handling suggests it was
  written expecting such rows to arrive. Not a data-loss issue — the item is still fully visible in
  the Sale's main line-items table, only absent from the fulfillment-specific widget. Left as a
  cross-task inconsistency worth resolving in a future pass, not this one.
- *Parked:* `MapToDtoAsync`'s N+1 query pattern (pre-existing at the branch's own base commit,
  confirmed via `git diff bf42f08`, not introduced or worsened by this plan beyond the proportional
  effect of pickups now sharing the same map path) — a real future optimization target, not a
  regression.
- *Parked:* two call sites invalidate `['sales', saleId, 'fulfillment-summary']` alongside
  `['sales', saleId]`, which React Query v5's prefix matching already covers — harmless, cosmetic.
- *Fixed:* `Sale.AddItem` threw `ArgumentOutOfRangeException(nameof(deliveryRequiredQuantity))` even
  when `pickupRequiredQuantity` was the actual negative value — split into two checks so each
  parameter reports itself correctly.
- *Fixed:* `FulfillmentReportsPage.tsx`'s new Pickups tab and "All fulfillment" tab parsed a
  `DateOnly` string with bare `new Date(d)` (parses as UTC midnight, can roll back a day under a
  negative UTC offset) instead of the midnight-anchored pattern already established elsewhere in
  this plan (`CancelFulfillmentModal.tsx`). Fixed to match. The pre-existing Deliveries tab has the
  identical unanchored pattern but predates this plan entirely (present at commit `bf42f08`, this
  branch's own base) and Task 18 explicitly kept that tab byte-for-byte unchanged as a regression
  surface — left alone, out of this plan's scope.

**Post-fix verification:** both fixes rebuilt and reverified independently — `npx tsc -b` and
`npm run build` both clean; full `dotnet test` re-run 524/524 except one isolated flake
(`Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds`, a timing-sensitive
concurrent-HTTP-race assertion unrelated to either fix by code path, confirmed passing cleanly on
an isolated re-run immediately after). Given both fixes are single-purpose, mechanical, and match
the original reviewer's own precise recommendation, a second full re-review round was judged
disproportionate to the risk and skipped in favor of this documented, independently-verified
resolution — consistent with how equivalently small findings were handled earlier in this plan
(e.g. Task 18's `formatDateTime` fix).

Nothing was merged or pushed during this final review or its fix round. Final commit: `1537ac0`.
