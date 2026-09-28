# RestoPOS Phase 0 — Design Specification

_2026-09-28_

## Status

Design approved in principle through brainstorming; **bill-level discount is explicitly NOT approved** (see Unresolved Decisions). No migrations or feature code have been written. This document is the record of that design pass, for review before an implementation plan is written.

## 1. Scope

RestoPOS is a new module alongside the existing Retail POS, supporting two service types a branch may enable independently or together:

- **Pay as You Order (PAYO):** take an order, complete payment and create a `Sale`, then release its kitchen ticket.
- **Bill Out:** open a table visit, send one or more kitchen rounds while unpaid, then settle one bill and create a `Sale`.

Both share one restaurant menu, modifiers, kitchen tickets, and reporting. **Resto sales support Void only** — Return must be rejected server-side and hidden in the UI. Explicitly out of scope for this phase: split bills, table merging, ingredient/recipe-level (BOM) inventory, physical kitchen printers, real-time (push) kitchen-display updates, and floor-plan/table-layout management.

---

## 2. Confirmed Code Findings

Everything in this section was read from the live codebase during this design pass, not inferred from stale docs. File:line references are current as of commit `7eaf9e4` (branch `master`).

### 2.1 Checkout, `Sale`, `SaleItem`

- `Sale` has **no Draft/Open/Held state** (`SaleStatus`: `Completed, Voided, Refunded, PartiallyRefunded`). A `Sale` row is only ever created already `Completed` — checkout builds and completes it inside one transaction (`CheckoutService.CheckoutAsync`, `src/Negosio.Application/Pos/CheckoutService.cs`, lines 55–228). There is no existing "open tab" concept anywhere in the domain.
- `Sale` requires a `RegisterSessionId` (non-nullable FK) and is hard-gated on that session being **open and owned by the calling user** (lines 74–91). Checkout never locks the session, only reads it.
- Stock deduction is atomic-with-checkout: an `UPDATE BranchInventories SET QuantityOnHand -= @qty WHERE QuantityOnHand >= @qty` (`InventoryPosting.DeductForSaleAsync`, `src/Negosio.Application/Inventory/InventoryPosting.cs`, lines 46–77), executed **after** the Sale/Items/Payments are already saved but still inside the same transaction. Zero rows affected throws `InsufficientInventory`, which rolls back everything, Sale included. There is no reservation/release window — deduction is strictly atomic-with-the-whole-transaction, not merely atomic-with-payment.
- Idempotency: `Sale.ClientRequestId` with a unique index on `(TenantId, ClientRequestId)` (`SaleConfiguration.cs` lines 62–63). A duplicate request's `SaveChangesAsync` loses the unique-constraint race, is caught, and the winner's Sale is returned instead of erroring.
- `SaleItem` snapshots product name/variant/SKU/barcode/cost/unit-price at checkout time so a later catalog edit never changes an old record (`SaleItem.cs`, doc comment lines 6–10). No free-text notes field exists on it.
- `SaleLineCalculator.Calculate` (`src/Negosio.Application/Pos/SaleLineCalculator.cs`) computes `Gross/Discount/Tax/Net` **per line**, and `Money.Round` (`src/Negosio.Application/Common/Money.cs`) documents its own invariant explicitly: *"Rounding is half-up at 2 decimal places, applied per line then summed, so displayed line totals always add up to the sale total."* There is no separate sale-level rounding or recompute pass. Discounts are line-level only (`DiscountKind`/`DiscountValue` on the line) — there is no existing order-level/bill-level discount concept anywhere in retail checkout today.

### 2.2 Void and Return

- **Void is whole-sale only.** No line-item or partial-quantity void exists anywhere in the codebase. `VoidSaleService.VoidAsync` reverses every `TrackInventory` line's full `Quantity` via `InventoryPosting.ReverseForVoidAsync` (no `>=` predicate — reversal always succeeds, even recreating a missing `BranchInventory` row). `Sale.Void()` is an all-or-nothing status transition (`Completed → Voided`), domain-guarded to only fire from `Completed`.
- Void takes a pessimistic lock — `SELECT 1 FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = @sessionId` — as the first statement in its transaction, to serialize against a concurrent session close (`VoidSaleService.cs` lines 87–89). `Sale.RowVersion` (EF `rowversion`, optimistic) separately catches a Void racing a Return.
- **Return is a structurally separate aggregate** (`SaleReturn`, its own table/number/child collections), line-item-level, gated by a single application-level check: `if (sale.Status is not (Completed or PartiallyRefunded)) throw BusinessRuleException(ReturnNotAllowed)` (`ReturnService.cs` lines 63–66). **There is no existing discriminator column on `Sale`** to distinguish "this came from Resto" — this is the one gap RestoPOS needs to fill for the Return prohibition.
- Payments/refunds are "recorded, not gateway-processed" (doc comments on `Payment.cs`/`RefundPayment.cs`) — void never reverses money, only status and stock. This is existing, unchanged behavior; Resto does not alter it.
- The authorization shape is identical and deliberately duplicated across `VoidAuthorizationResolver`, `ReturnAuthorizationResolver`, and `CheckoutService.EnsureDiscountAuthorizedAsync`: role allow-list (Owner/Admin/Manager/Cashier only), Owner/Admin/Manager pass directly, Cashier needs a `UserPermission` grant (`IUserPermissionGrantService.HasGrantAsync`) or a live manager-password-verified approval (`IApproverVerificationService`). Each is its own class "so a change to one action's error codes/messages can never accidentally affect another."

### 2.3 RegisterSession and locking precedent

- Exactly one `Open` session per register and one `Open` session per user, enforced by two filtered unique indexes (`RegisterSessionConfiguration.cs` lines 45–55) with pre-check + `DbUpdateException` catch as the race-safe backstop.
- `RegisterSession` has **no RowVersion** — closing it uses the same `WITH (UPDLOCK, HOLDLOCK)` pattern as Void, from the other side, so whichever of {void, close} arrives first runs to completion before the other can even read (`RegisterSessionService.ReconcileAndCloseAsync`, lines 111–184).
- A structurally identical lock is used in `DeliveryReceiptService.LockSaleItemsAsync` (lines 690–713) specifically because "RowVersion only protects an UPDATE against a row that already exists — it cannot catch two concurrent create calls each INSERTing a brand-new row." This is the direct precedent for "two staff editing the same open table," which is exactly that shape of problem (concurrent inserts against a shared parent).
- The cash-reconciliation-at-close formula sums `Payment`/`RegisterCashMovement`/`RefundPayment` rows by `RegisterSessionId` (`RegisterSessionService.cs` lines 148–181) — if Resto settlement produces ordinary `Payment` rows against the session, this formula needs no changes.

### 2.4 Reporting precedent

- Every report method is server-side SQL aggregation over `Sale`/`SaleItem`/`Payment`/`SaleReturn`, gated through `Qualifying()` (excludes `Status == Voided`), and none of the core KPI reports (`GetOverviewAsync`, `GetBranchPerformanceAsync`, `GetRegisterPerformanceAsync`, `GetCashierPerformanceAsync`) branch on any per-sale discriminator today — they simply sum `Sale.Subtotal`/`GrandTotal`/etc. regardless of how the sale originated.
- The established pattern for adding a new dimension (proven by Delivery/Pickup) is: a new enum/discriminator on the *line or a satellite entity*, never retrofitted as branching logic inside the existing KPI reports; and **additive new report methods** (`GetDeliveriesAsync`, `GetPickupsAsync`, etc.) rather than modifying existing ones.
- **Confirms:** adding `Sale.Origin` as an unfiltered-by-default column will *not* change what the existing Overview/Branch/Register/Cashier reports include — Resto sales flow into those totals automatically, with no code change required there. This was verified, not assumed.

### 2.5 Catalog, branch, and role gaps

- **No modifier concept exists.** One price per `ProductVariant`, by deliberate ADR (`docs/adr/0001-single-sellable-item-model.md`) — SKU/Barcode uniqueness is enforced by a single filtered index per column on `ProductVariant`, which is why the ADR rejected splitting pricing across two tables.
- **No branch feature-toggle pattern exists.** `Branch` has only `IsActive`. The closest precedent is `TenantProfile.ConfigureTax(...)` — plain booleans plus a `Configure*` domain method on the owning entity.
- **No kitchen-ticket/KOT concept exists anywhere** (confirmed by an exhaustive grep). `ReceiptService`/`ReceiptSettings` are structurally Sale-derived and cannot represent something printed/displayed before a Sale exists.
- **No background-job, outbox, message-queue, or retry-policy infrastructure exists at all** (confirmed by an exhaustive search of `src/` and every `.csproj`). The only reliability precedent in this codebase is a synchronous unique-constraint idempotency key checked inline (`Sale.ClientRequestId`, and a similar unique-constraint-as-retry-guard on `FulfillmentConversion`).
- `UserRole.KitchenStaff` already exists in the enum, branch-scoped, with **zero behavior wired to it today**.
- `UserPermissionGrantService.SetAsync` currently restricts grants to targets holding `UserRole.Cashier` only (`UserPermissionGrantService.cs` lines 64–67) — relevant if any future Resto permission needs to be *grantable* to `KitchenStaff` rather than a role-level capability.
- The nearest precedent for "checkout succeeded, a dependent follow-up step failed independently" is the existing Delivery/Pickup flow: checkout and schedule-creation are already two separate calls/transactions for exactly this reason, with a "Schedule now" recovery action shown on Sale Detail when a sale exists but its schedule doesn't. This is the direct precedent for RestoPOS's own payment/kitchen-release reliability design (Section 6).
- This codebase is **database-per-tenant** (separate Platform DB + one DB per tenant, per-request connection routing) — confirmed relevant at runtime (the API startup log queries a `TenantDatabases` table in the Platform DB). Any cross-tenant background process must explicitly enumerate tenant databases; there is no ambient "current tenant" outside a request scope.

---

## 3. Proposed Domain Model

All of the following are net-new. None of it modifies existing Retail entities except the two additions noted (`Sale.Origin`, `Branch` service-type flags).

**`RestoStation`** — branch-scoped. `Name`, `IsActive`. Configurable per branch (Kitchen, Bar, Dessert, Grill, whatever the tenant runs), not a hardcoded enum.

**`RestoTable`** — branch-scoped. `Name`, `IsActive`. No floor-plan/position fields.

**`RestoOrder`** — the pre-Sale aggregate, shared by both service types:
- `ServiceType` (PayAsYouOrder | BillOut)
- `TableId` (required for BillOut, must be null for PAYO), `DisplayLabel` (free text, mainly PAYO)
- `Status` (Open | Settled | Cancelled)
- `RegisterSessionId`, `OpenedByUserId`, `OpenedAtUtc`
- `SettledAtUtc?`, `SaleId?` (set only at settlement)
- `CancelledAtUtc?`, `CancelledByUserId?`, `CancelReason?`
- `RowVersion` (client-sent expected-version conflict detection — see Section 7)
- A filtered unique index: **at most one `Open` `RestoOrder` per `TableId`** — mirrors `RegisterSession`'s existing "one Open session per Register" index exactly, and is what actually prevents two concurrent visits on the same table.

**`RestoOrderRound`** (child of `RestoOrder`) — deliberately thin: `RoundNumber`, `Status` (`Draft`, `Released`, or `Voided` — `Voided` is reachable only from `Draft`, a whole round scrapped before it was ever sent; a round that has reached `Released` can never subsequently become `Voided` or return to `Draft`), `ReleasedAtUtc?`, `ReleasedByUserId?`. Release is a durable, timestamped event; a released round is never un-released.

**`RestoOrderItem`** (child of Round) — the snapshot discipline matches `SaleItem`'s exactly, taken once at add-time and never re-derived:
- `ProductNameSnapshot`, `VariantNameSnapshot?`, `StationId` + `StationNameSnapshot` (both retained even if the station is later renamed/disabled)
- `UnitPriceSnapshot`, `TaxRateSnapshot`, and the **frozen computed line result** — `GrossAmount`/`DiscountAmount`/`TaxAmount`/`NetAmount`, computed once via the existing `SaleLineCalculator.Calculate` at add-time and copied verbatim into the resulting `SaleItem` at settlement (never recomputed)
- `Quantity`, `KitchenNote`
- `KitchenStatus` (Pending → Acknowledged → Ready → Served), meaningful once the parent round is Released, plus `AcknowledgedAtUtc/By`, `ReadyAtUtc/By`, `ServedAtUtc/By`
- Void fields that never delete the row: `VoidedAtUtc/By`, `ApprovedByUserId?`, `VoidReason?` — a voided item stays visible in its round for the audit trail; settlement skips voided items when building the Sale. **Whole-line void only** for Phase 0 (no partial-quantity void) — to reduce a line's quantity, staff void the line and add a new line for the corrected quantity.

**`RestoOrderItemModifier`** (child of Item) — `ModifierGroupNameSnapshot`, `ModifierOptionNameSnapshot`, `PriceDeltaSnapshot`, same snapshot discipline.

**`ModifierGroup`** / **`ModifierOption`** / **`ProductModifierGroup`** (join) — tenant-owned, reusable across menu items (e.g. one "Add-ons" group attachable to many products). `ModifierGroup.SelectionType` (Single | Multiple).

**`Sale.Origin`** (`Retail | Resto`) — one new column, set once at creation, never changed. See Section 8.

**`Branch.SupportsPayAsYouOrder` / `SupportsBillOut`** — plain booleans, mutated via `ConfigureRestoServiceTypes(...)`, following `TenantProfile.ConfigureTax`'s precedent.

---

## 4. Lifecycle Rules

**Pay-as-you-order:** `RestoOrder` opened (ServiceType=PAYO, TableId null) → items added to its single round (Draft) → **settlement is required before that round may be released** → settlement builds the `Sale` from the round's non-voided items, sets `Origin=Resto`, flips `Status=Settled` with `SaleId` set — all in one transaction → round release is then attempted as a separate, immediately-following step (see Section 6 for what happens if it fails).

**Bill-out:** `RestoOrder` opened (ServiceType=BillOut, TableId required) → round 1 items added → round released to kitchen **while `RestoOrder.Status` is still `Open`, unpaid** → optionally more rounds, each independently released → staff settles → settlement builds one `Sale` from every non-voided item across every round, in the same one-transaction shape as PAYO.

**Settlement, either flow:** lock the `RestoOrder` (Section 7), re-validate `Status == Open`, build `Sale`/`SaleItem`/`Payment` rows from all non-voided `RestoOrderItem`s across all rounds using their already-frozen computed amounts (no recalculation), run the *exact* stock-deduction/sale-numbering pipeline `CheckoutService` already uses, set `Origin=Resto`, flip `RestoOrder.Status=Settled` with `SaleId` — **all in the same transaction**, which is what makes table-occupancy release automatic: the moment `Status` leaves `Open`, the filtered unique index on `TableId` stops counting the row, with no separate "release occupancy" step to forget or get out of order.

---

## 5. Stock, Void, Waste, and Failed-Payment Effects

- **Stock deducts exactly once, at settlement** — identically to Retail checkout, via the same `InventoryPosting.DeductForSaleAsync`. Sending a kitchen round to the kitchen **never touches inventory** in this design; nothing is reserved or released, because nothing is deducted until a `Sale` actually exists. (This directly answers the "stock effect of a kitchen round" question: there isn't one, by design — the earlier "at payment" decision was deliberately kept, and this section exists to state that plainly rather than leave it implicit.)
- **Item void before settlement** has zero stock effect, at any `KitchenStatus`, because nothing was ever deducted for it. Settlement simply excludes voided items when building the Sale.
- **Prepared waste** (a fired-and-even-served item later voided) — Phase 0 records it purely as an audited void event (reason, actor, approval tier) with **no separate cost/loss ledger** (per your earlier answer) and **no stock movement** (nothing was deducted to reverse). If the business later wants to quantify kitchen-waste cost, that's an additive reporting feature on top of the existing void-reason data, not a new stock concept.
- **Failed/insufficient payment at settlement** — caught by the exact same `ResolvePayments` validation checkout already uses, before the transaction even opens; stock is never touched in that failure mode, and the `RestoOrder` stays `Open` (nothing was settled, nothing to unwind).
- **Full-sale void of a settled Resto sale** — no new code. A settled Resto sale is an ordinary `Sale` row; the existing `VoidSaleService`/`ReverseForVoidAsync` reverses every `TrackInventory` line's full `Quantity`, which is correct by construction because only non-voided-at-order-level items ever became `SaleItem`s in the first place (settlement excluded voided ones, so there's nothing to over-reverse). Payments are not reversed by void — unchanged from existing Retail behavior. **This is a confirmed-by-design-logic claim, not yet a tested one** — see Milestone 2's acceptance criteria.

---

## 6. Reliability

### 6.1 Payment succeeds, kitchen release fails or is delayed (PAYO only)

Round release is its own idempotent operation: a status guard (`if already Released, no-op`), never a duplicate ticket. Two layers handle the failure:

1. **Manual recovery action** — the Order/Sale detail view detects "Settled, has a Sale, round still Draft" and shows a "Release kitchen ticket" button, mirroring the proven Delivery/Pickup "Schedule now" recovery pattern.
2. **Automatic reconciliation** — because a paid customer cannot be left waiting on a cashier noticing a button, a small `BackgroundService` (built-in ASP.NET hosting — genuinely new infrastructure for this codebase, not reused from anywhere) polls periodically for `Settled` PAYO orders whose round is still `Draft` past a short grace period, and calls the same idempotent release operation. A persistent "release pending" alert, fed by the same durable state, stays visible to front-of-house until the round actually flips to `Released`, by whichever of {worker, manual button} gets there first.

**This worker must be tenant-aware.** The codebase is database-per-tenant with no ambient "current tenant" outside a request — the worker must explicitly enumerate every provisioned tenant database (via the Platform DB's tenant registry) and run its reconciliation query scoped to each one in turn. **Multi-instance safety is free, not something the worker needs to coordinate itself:** if two app instances' workers both wake up and race to release the same round, the release operation's own conditional update (`WHERE Status = Draft`) means only one instance's `UPDATE` affects a row; the other affects zero rows and simply moves on — the same idiom already used for the manual button, requiring no additional locking or leader-election.

### 6.2 "Released" is not "seen by the kitchen"

`Pending` already means "sent, not yet acknowledged" in the `KitchenStatus` state machine — `Acknowledged` requires an actual kitchen-side action. Front-of-house gets a visible age indicator computed from the round's `ReleasedAtUtc` ("sent 6 minutes ago, not yet acknowledged"), fed by durable state, no new fields required for that specific computation. A reconnecting/refreshing KDS has nothing to "miss" — it always re-polls current durable truth rather than a stream that could drop an event.

### 6.3 Concurrency

Every structural write against an open order (add item, void item, release round, settle, cancel) opens its transaction with the order-level pessimistic lock **first**, then touches item rows — never the reverse, across every one of those five operations, to keep lock ordering consistent and deadlock-free. The lightweight KDS ack/ready/served transitions never take the order lock at all (single-row conditional updates, no cross-row invariant to protect).

The client is **required** to send its last-seen `RestoOrder.RowVersion` on every structural write; the server checks it immediately after acquiring the pessimistic lock (not before — the lock closes the check-then-act gap) and returns a hard conflict, not a silent overwrite, on mismatch. KDS item transitions are single atomic conditional updates (`WHERE Id=@item AND KitchenStatus=@expected AND VoidedAtUtc IS NULL`) so a "mark ready" cannot land on an item a manager just voided, and vice versa.

---

## 7. Return Prohibition

`Sale.Origin` (`Retail | Resto`) is the only new column needed. `ReturnService.CreateReturnAsync`'s existing single eligibility check gains one more condition — `Origin == Resto` throws `BusinessRuleException(ErrorCodes.ReturnNotAllowedForResto)` — matching the fact that this is already the one and only server-side enforcement point today. The frontend additionally hides the Return action for a Resto-origin sale, as defense in depth. **Void requires zero code changes** — it doesn't know or care about `Origin`.

---

## 8. Permissions and Branch Flags

New `UserPermission` values, each its own enum member (never overloading an existing one, per this codebase's explicit convention):

- `RestoItemVoid` — voiding an already-*Released* item. Owner/Admin/Manager direct; Cashier needs a grant or live approval, same shape as `SalesVoid`. Voiding a still-`Draft` item needs no special permission.
- `RestoOrderCancel` — cancelling a whole order. **Only valid while the order has zero Released rounds** (see Section 10 — once anything has been sent to the kitchen, cancellation is no longer the right tool).

`KitchenStaff` gets ack/ready/served capability on the KDS as a **role-level** capability, not a granular grant — this avoids touching `UserPermissionGrantService`'s current Cashier-only grant-target restriction for this phase. Branch-scoped like every other role.

`Branch.SupportsPayAsYouOrder`/`SupportsBillOut` are enforced at order-creation (reject an unsupported service type) and reflected in the UI (hide the disabled entry point).

---

## 9. Reporting and Receipts

- **Confirmed:** Overview/Branch/Register/Cashier KPI reports need zero code changes — they sum Sale-level fields with no `Origin` branching today, so Resto sales are included automatically the moment they exist as ordinary `Sale` rows.
- New Resto-specific reports (e.g. a Resto sales breakdown, per-service-type KPIs) are **additive new methods**, following the Delivery/Pickup precedent — never branches inside the existing KPI methods.
- `ReceiptService`/`ReceiptSettings` need no structural changes to print a settled Resto sale's receipt — it's an ordinary Sale/SaleItem/Payment set. **See Unresolved Decisions** for the modifier-itemization gap this surfaces.

---

## 10. Unresolved Decisions Requiring Product Input

These are explicitly **not** decided by this document. Each needs your sign-off before the relevant milestone starts.

1. **Bill-level discount — deferred, not approved.** A bill-level discount would make `Sale.DiscountTotal` exceed the sum of its `SaleItem` discounts, an implicit invariant that reports, receipts, tax totals, and possibly void logic may assume holds. **Recommendation for this release: line-level discounts only**, reusing the existing calculation and authorization path exactly as Retail does today. Bill-level discounts should be a separate design pass, done only after explicitly tracing every consumer of `Sale.DiscountTotal`/`Sale.GrandTotal` (reports, receipts, void, any tax filing export) to confirm none of them would silently misbehave.

2. **Bill-Out cancellation once food has been prepared or served.** Authorization (`RestoOrderCancel`) is a necessary but not sufficient answer — it doesn't resolve the accounting question. Proposed rule (needs sign-off, not yet approved): **`RestoOrderCancel` is only valid while the order has zero Released rounds** — a true "nothing happened yet" cancel, fully discardable with just an audit log entry. Once anything has been sent to the kitchen, the order can no longer be silently cancelled; it must go through Settlement — even for a walk-out where nothing is actually collected — because Settlement is the only path that correctly creates an auditable `Sale` and deducts the stock that was, in reality, consumed. Open question: what should a $0/write-off settlement look like concretely (a `Sale` with `GrandTotal=0` and a mandatory reason/approval? a distinct write-off flag?) — I'd recommend keeping it inside the ordinary Sale pipeline for stock/reporting consistency, but this needs explicit product input, not an assumed default.

3. **Modifier itemization on receipts and reports.** `SaleItem` has no field to carry a modifier summary — a settled Resto sale's receipt line would show only the item name and a price that silently includes modifier deltas, with no "no onions, extra cheese" breakdown visible to the customer or in reports. Needs a decision: add a modifier-summary snapshot field to `SaleItem` (new column, straightforward given `RestoOrderItemModifier` already has the frozen data to copy from), or accept no modifier detail on the receipt/reports for this phase.

4. **Partial-quantity item void.** Deferred per your earlier answer (whole-line void only; staff void-and-re-add to change a quantity). Flagging again here only so it isn't forgotten as a possible Phase 1 usability follow-up if voiding-and-re-adding proves awkward in practice.

---

## 11. Milestones and Acceptance Criteria

**M1 — Domain + migrations.** `RestoStation`, `RestoTable`, `RestoOrder`, `RestoOrderRound`, `RestoOrderItem`, `RestoOrderItemModifier`, `ModifierGroup`/`ModifierOption`/`ProductModifierGroup`, `Sale.Origin`, `Branch` service-type flags. No behavior.
*Acceptance:* migrations apply cleanly against a copy of the current schema; the filtered unique index on `RestoOrder(TableId) WHERE Status=Open` is verified to actually reject a second concurrent open order against the same table at the DB level (not just in application code).

**M2 — Order lifecycle services.** Open order, add/void item (frozen `SaleLineCalculator` snapshot), release round (idempotent), settle (atomic, reuses checkout's stock/payment/numbering pipeline — **line-level discount only**, per the unresolved-decision default), cancel (zero-released-rounds rule).
*Acceptance:* an integration test proves a settled Resto sale, when voided via the existing unmodified `VoidSaleService`, reverses exactly the stock quantities that were deducted at settlement — closing the "confirmed by design logic, not yet tested" gap from Section 5. A second test proves settlement's line totals exactly match what was displayed to staff during ordering (the frozen-snapshot guarantee), byte-for-byte against `SaleLineCalculator`'s own output.

**M3 — Reliability and reconciliation.** The manual release-recovery action, the tenant-enumerating background reconciliation worker, the front-of-house unacknowledged-ticket alert.
*Acceptance:* a test proves two concurrent release attempts (simulating two app instances) against the same round produce exactly one `Released` transition and no duplicate kitchen ticket; a test proves the worker processes more than one tenant database in a single pass without cross-tenant leakage.

**M4 — Authorization.** `RestoItemVoid`/`RestoOrderCancel` resolvers, branch-flag enforcement at order creation, `KitchenStaff` role wiring.

**M5 — KDS.** Polling read API, per-station filtering by `StationId`, conditional-update ack/ready/served endpoints (with the void-race guard from Section 6.3).

**M6 — Reporting and receipts.** Additive Resto report methods; verify (with a test, not just inspection) that `GetOverviewAsync` et al. correctly include a Resto sale's totals unchanged; resolve and implement whichever answer to Unresolved Decision #3 (modifier itemization) was chosen before this milestone starts.

**M7 — Frontend.** Menu/modifier picker, order-taking + table list, settlement/bill screen, KDS view, hide Return for Resto sales, Staff Permissions UI additions for the two new permissions.
