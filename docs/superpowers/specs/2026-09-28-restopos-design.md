# RestoPOS Design Specification

_Originally 2026-09-28. Revised 2026-10-06 for Milestone 2 (order lifecycle services). Verified against `master` @ `26643ce`._

## Status

- **M1 (domain + migrations): merged and pushed.** Entities, enums, EF mappings, `Sale.Origin`, `Branch` service flags, migration `AddRestoPos`. No behavior.
- **M2 (order lifecycle services): specified here, not implemented.** This revision resolves the product decisions the user approved on 2026-10-06 and specifies the remaining service behavior, invariants, and inventory policy. Implementation plan: `docs/superpowers/plans/2026-10-06-restopos-m2-order-lifecycle.md`.
- Remaining product decisions that could not be resolved from approved requirements or existing code are listed in Section 10.

## 1. Scope

RestoPOS is a restaurant service module alongside Retail POS. A branch may enable either or both service types:

- **Pay as You Order (PAYO):** the order is settled (payment taken, `Sale` created) before its single round is released to the kitchen.
- **Bill Out:** a table visit is opened; one or more rounds are released to the kitchen while unpaid; the bill is then settled into one `Sale`, or the visit is closed unpaid under an explicit, audited, approved closure.

Resto sales support **Void only**. Return is rejected server-side for Resto-origin sales and hidden in the UI.

Out of scope for this phase: split bills, table merging, bill-level discounts, partial-quantity item voids, ingredient/recipe (BOM) inventory, physical kitchen printers, push-based kitchen display, floor-plan management, restaurant-specific reports, frontend. The KDS, the background reconciliation worker, and the restaurant UI belong to M3, M5, and M7.

## 2. Confirmed Code Findings

Verified against `master` @ `26643ce`. Where this section corrects the 2026-09-28 findings, the correction is marked **[corrected]**.

### 2.1 Checkout, `Sale`, `SaleItem`

- `CheckoutService.CheckoutAsync` (`src/Negosio.Application/Pos/CheckoutService.cs`) runs: idempotency pre-check on `Sale.ClientRequestId` → branch and register-session validation (session must be `Open` and owned by the caller) → discount authorization if any line has a discount → **merge duplicate variant lines** → per-line `SaleLineCalculator.Calculate` → sale totals → `ResolvePayments` → **one transaction**: `DocumentNumberService.NextAsync` → `Sale.Begin`/`AddItem`/`AddPayment`/`Complete` → `SaveChangesAsync` (duplicate `ClientRequestId` race caught and the winner returned) → `InventoryPosting.DeductForSaleAsync` per `TrackInventory` line → `SaveChangesAsync` → commit.
- **Sale totals are sums of frozen per-line results:** `subtotal = Σ Gross`, `discountTotal = Σ Discount`, `taxTotal = Σ Tax`, `saleTotal = PricesIncludeTax ? subtotal − discountTotal : subtotal − discountTotal + taxTotal`, `grandTotal = saleTotal + deliveryCharge`. There is no sale-level rounding pass.
- **[corrected] Retail checkout takes no register-session lock.** Void, session close, cash movements, and cash drawer do. Checkout reads session status before its transaction only. This is a pre-existing race (a checkout concurrent with a close can land after the close computed expected cash). Out of M2 scope; flagged in Section 10.
- `ResolvePayments` is a **private static method** inside `CheckoutService`. Settlement needs it; it must be extracted, not copied (Section 6).
- `SaleItem` snapshots name/SKU/barcode/unit price/cost at creation. **[corrected]** `SaleItem` has only **non-unique** indexes on `(TenantId, SaleId)` and `(TenantId, ProductVariantId)` (`SaleItemConfiguration.cs`). The same variant may legitimately appear on several lines of one sale. Resto relies on this to keep "Burger + cheese" and "Burger plain" as separate lines.
- `SaleLineCalculator.Calculate` (`src/Negosio.Application/Pos/SaleLineCalculator.cs`) rounds half-up per line via `Money.Round`. Discounts are line-level only (`DiscountType` + `DiscountValue` on the line). **[corrected]** The calculator reads `TaxRatePercent` and `PricesIncludeTax` from the tenant at call time, so these must be frozen per order (Section 3).
- `Sale.Complete(...)` stamps `CompletedAtUtc = DateTime.UtcNow` and does **not** take a time parameter. Settlement inherits this; tests that depend on the same-day void rule must account for it (Section 9).
- `Sale.Begin(...)` already accepts `SaleOrigin origin` (M1). `Sale.ClientRequestId` has a unique index on `(TenantId, ClientRequestId)`.

### 2.2 Void and Return

- **Void is whole-sale only.** `VoidSaleService.VoidAsync` (`src/Negosio.Application/Sales/VoidSaleService.cs`) runs: role gate → validation → load sale → branch guard (404 for a foreign branch) → `VoidEligibility.Evaluate` fast-fail → `IVoidAuthorizationResolver` → **one transaction**: `UPDLOCK,HOLDLOCK` on `RegisterSessions` → eligibility re-check → `CancelActiveScheduleForVoidedSaleAsync` → `ReverseForVoidAsync` for every `TrackInventory` line at its full `Quantity` → `Sale.Void(...)` → `SaveChangesAsync` (`DbUpdateConcurrencyException` on `Sale.RowVersion` → rollback and re-evaluate) → commit.
- **[corrected]** The comment at `VoidSaleService.cs` lines 84–86 says `ReconcileAndCloseAsync` "does not yet" take the matching lock. It does now (`RegisterSessionService.cs`, the `UPDLOCK,HOLDLOCK` read at the start of `ReconcileAndCloseAsync`). The comment is stale; the behavior is correct.
- `VoidEligibility.Evaluate` order: has returns → not `Completed` → session not open → not the same UTC day. This applies to every sale, including Resto.
- **Return** is a separate aggregate. `ReturnService` (`src/Negosio.Application/Sales/ReturnService.cs`) checks `sale.Status is Completed or PartiallyRefunded`, then the authorization resolver. No existing column distinguishes Resto sales; `Sale.Origin` (M1) is the discriminator.
- Payments and refunds are **recorded, not gateway-processed**. Void never reverses money.
- `IVoidAuthorizationResolver`, `IReturnAuthorizationResolver`, `IFulfillmentCancelAuthorizationResolver`, `ICashMovementAuthorizationResolver` each mirror one shape: Owner/Admin/Manager act directly; a Cashier needs its `UserPermission` grant or a verified Manager/Admin/Owner approval (`IApproverVerificationService`). The shared approval input is `VoidSaleApprovalInput`.

### 2.3 Inventory

- **[corrected]** `BranchInventory.ApplyAdjustment` **never allows a negative result** (domain throws). `InventoryPosting.DeductForSaleAsync` uses a conditional `UPDATE ... WHERE QuantityOnHand >= @qty` and throws `InsufficientInventory` on zero rows affected. Every stock decrement path must respect this invariant. This constrains the waste policy (Section 5).
- `InventoryPosting.ReverseForVoidAsync` and `RestockForReturnAsync` never fail and recreate a missing `BranchInventory` row.
- `StockMovementType.Waste` (value 9) **exists and has no producer anywhere in the codebase.** M2 uses it. No new enum value is needed.
- `StockMovement.Create(...)` takes `referenceType` and `referenceId`, so a waste event can point at a `RestoOrder` or `RestoOrderItem`.

### 2.4 Register sessions and locking

- One `Open` session per register and one per user (filtered unique indexes). `RegisterSession` has no RowVersion.
- Pessimistic lock precedent: `SELECT 1 FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = @id`, first statement in the transaction. Used by Void, session close, cash movements, cash drawer. `DeliveryReceiptService.LockSaleItemsAsync` uses the same shape on a sale's items, because RowVersion cannot catch two concurrent INSERTs.
- The reconciliation formula at close sums `Payment`, `RegisterCashMovement`, and `RefundPayment` rows by `RegisterSessionId`. Settlement writes ordinary `Payment` rows and needs no formula change.

### 2.5 Reporting

- KPI reports (`GetOverviewAsync`, `GetBranchPerformanceAsync`, `GetRegisterPerformanceAsync`, `GetCashierPerformanceAsync`) sum `Sale`-level fields with no `Origin` branching. Resto sales are included automatically once they exist as `Sale` rows. Verified, to be proven by test in M6.

### 2.6 M1 entity gaps found during this review

The M1 entities are the starting point. Reviewing them against checkout surfaced the following. Each is resolved in Section 3 or Section 5.

| # | Gap | Where | Resolution |
|---|---|---|---|
| G1 | `RestoOrderItem` stores no discount kind/value, so the Sale item cannot reproduce `DiscountType`/`DiscountValue`. | `RestoOrderItem.cs` | Add `DiscountKind`, `DiscountValue`, `DiscountApprovedByUserId`. |
| G2 | No cost snapshot, so `SaleItem.CostPriceSnapshot` cannot be populated. | `RestoOrderItem.cs` | Add `CostPriceSnapshot`. |
| G3 | `PricesIncludeTax` is not frozen. If a tenant toggles it between open and settle, the Sale total formula changes. | `RestoOrder` / `TenantProfile` | Freeze `PricesIncludeTaxSnapshot` on `RestoOrder` at open. |
| G4 | `RestoOrderRound.Release` and `RestoOrderItem.Void` do not check the parent order's status (the domain cannot see it). | Domain | Application service enforces status before calling them (Section 6). |
| G5 | `RestoOrderItem.Void` decides waste from `KitchenStatus` read before the write. A KDS transition could change `KitchenStatus` in between. | Concurrency | Void is a conditional update on the observed `KitchenStatus` (Section 6.3). |
| G6 | No modifier snapshot exists on `SaleItem`. | `SaleItem` | New child table `SaleItemModifiers` (Section 3). |
| G7 | No settlement idempotency key exists on `RestoOrder`. | `RestoOrder` | Add `SettlementRequestId` (Section 6.2). |
| G8 | `RestoOrderStatus` has no state for unpaid closure. | `RestoOrderStatus` | Add `UnpaidClosed = 4` (int column, no migration for the enum itself). |

---

## 3. Domain Model

M1 entities are unchanged in identity and shape. M2 adds the fields below. All new fields are additive and nullable or defaulted, so no existing row needs a backfill that changes meaning.

### 3.1 `RestoOrder` (additions)

- `PricesIncludeTaxSnapshot` (bool, set at open from `TenantProfile.PricesIncludeTax`, never changed). Every item on the order is priced with this value (G3).
- `SettlementRequestId` (Guid?, client-supplied, unique per tenant when non-null). Settlement idempotency key (G7).
- `UnpaidClosedAtUtc`, `UnpaidClosedByUserId`, `UnpaidClosureReason`, `UnpaidClosureApprovedByUserId` (G8).
- `UnpaidClosureRequestId` (Guid?, unique per tenant when non-null). Idempotency key for closure.
- `Status` gains `UnpaidClosed = 4`.

Transition table (the only legal transitions):

| From | To | Operation | Preconditions |
|---|---|---|---|
| — | `Open` | Open order | Branch flag for the service type is `true`. Caller has an open register session on the branch. BillOut: `TableId` required and no other `Open` order on that table (DB index is the backstop). |
| `Open` | `Open` | Add round, add item, add modifier, void Draft item, release round (BillOut) | Order is `Open`. |
| `Open` | `Open` | Void Released item | Order is `Open`. Waste policy in Section 5. |
| `Open` | `Cancelled` | Cancel unsent order | Order is `Open`. **Zero** Released rounds. No stock effect. |
| `Open` | `UnpaidClosed` | Unpaid closure | BillOut only. Order is `Open`. At least one Released round. Section 5.3. |
| `Open` | `Settled` | Settle | Order is `Open`. No Draft rounds with non-voided items (Section 6.2). At least one non-voided item. |
| `Settled` | `Settled` | Release round (PAYO) | Round is `Draft`. PAYO only. Idempotent (Section 6.4). |
| `Settled` | `Settled` | Release round (PAYO recovery) | Same. |

Illegal and rejected: any transition out of `Settled`, `Cancelled`, or `UnpaidClosed` (except the PAYO release above); any round-level write on a non-`Open` order other than the PAYO release; BillOut settlement with a `Draft` round that has non-voided items; PAYO release before settlement; cancel with any Released round; unpaid closure with zero Released rounds; a second settlement or closure with a different key.

### 3.2 `RestoOrderRound`

No field changes. Round status transitions: `Draft → Released`, `Draft → Voided` (whole round scrapped before release), `Released` is terminal. A `Draft` round may not be released on a non-`Open` order (G4, enforced in the service).

### 3.3 `RestoOrderItem` (additions)

- `DiscountKind` (`DiscountType`), `DiscountValue` (decimal). Frozen from the line input at add-time. The computed `DiscountAmount` is already stored (G1).
- `DiscountApprovedByUserId` (Guid?). The approver when a Cashier without `DiscountApply` needed approval (G1).
- `CostPriceSnapshot` (decimal?). From `ProductVariant.CostPrice` at add-time (G2).
- Existing `UnitPriceSnapshot` **is the effective unit price**: base variant price plus the sum of its modifier deltas. This keeps the Sale invariant `GrossAmount = UnitPrice × Quantity` (Section 4.2).

### 3.4 `RestoOrderItemModifier` (no change)

Already holds group name, option name, and price delta snapshots. Settlement copies these into `SaleItemModifier` rows.

### 3.5 Sale side (new)

- **`SaleItemModifier`** (new table `SaleItemModifiers`): `TenantId`, `SaleItemId`, `ModifierGroupNameSnapshot`, `ModifierOptionNameSnapshot`, `PriceDeltaSnapshot`, `SortOrder` (int). Backward compatible: existing retail `SaleItem` rows have no modifier rows. Receipt rendering of modifiers is in M6 and depends on Section 10 decision D5.
- `Sale.Origin` (`SaleOrigin.Resto`) is set by settlement only.

### 3.6 Branch flags

`Branch.SupportsPayAsYouOrder` and `Branch.SupportsBillOut` already exist (M1). Checked at order open only. Changing a flag after an order is open does not affect that order.

---

## 4. Pricing and Reconciliation

### 4.1 Frozen line computation

At add-item time, the application service calls the existing `SaleLineCalculator.Calculate(effectiveUnitPrice, quantity, discountType, discountValue, taxRate, pricesIncludeTax)` where:

- `effectiveUnitPrice = variant.SellingPrice + Σ modifier.PriceDelta` (each modifier's delta read from the live `ModifierOption` at add-time and snapshotted);
- `taxRate` and `pricesIncludeTax` are the **order's** frozen snapshot values (`TaxRateSnapshot` on the item, `PricesIncludeTaxSnapshot` on the order), not live tenant values at settlement;
- `discountType` and `discountValue` are the line's input, authorized as in Section 8.

The resulting `Gross`, `Discount`, `Tax`, `Net` are stored on the item and never recomputed.

### 4.2 Settlement totals

Settlement builds the Sale from non-voided items only, using the same formula checkout uses, extracted into one shared function (Section 6.5):

```
subtotal      = Σ item.GrossAmount            (non-voided items)
discountTotal = Σ item.DiscountAmount
taxTotal      = Σ item.TaxAmount
saleTotal     = pricesIncludeTax ? subtotal − discountTotal : subtotal − discountTotal + taxTotal
grandTotal    = saleTotal                     (no delivery charge on Resto sales)
```

`pricesIncludeTax` here is `RestoOrder.PricesIncludeTaxSnapshot`.

**Reconciliation guarantee (tested in Section 9):** the order's displayed summary is computed from exactly the same function over the same non-voided items. Therefore the grand total staff saw while ordering equals the settled `Sale.GrandTotal` byte-for-byte, provided no item was voided between the two (and a void changes both sides identically).

Because every line is already rounded to 2 dp and the sums are exact decimal additions, no rounding step is applied at sale level. This matches checkout.

### 4.3 Multi-round billing

All rounds contribute items to the same totals. Settlement reads items across all rounds of the order in one query, excluding voided items and voided rounds. A Draft round is never settled (Section 6.2).

---

## 5. Inventory Policy

### 5.1 Principles

1. **A stock unit leaves inventory exactly once** through exactly one of three mutually exclusive outcomes for a given order item:
   - **Sale** (settlement): deducted via `InventoryPosting.DeductForSaleAsync`, referenced to the new `Sale`.
   - **Waste** (item void after release, or unpaid closure of a released item): deducted via a new `InventoryPosting.DeductForWasteAsync`, referenced to the `RestoOrderItem` or `RestoOrder`.
   - **Nothing** (never released and then voided or cancelled; or Pending released items at unpaid closure, Section 5.3): no movement.
2. **Exactly-once is enforced by the item's terminal state**, not by a stock ledger check. An item is either `VoidedAtUtc != null` (never settled, waste already taken if applicable), or it is settled into a Sale (and is then never voided at order level), or it is removed by unpaid closure/cancel. Settlement reads only non-voided items under the order lock, so an item cannot be both sold and wasted.
3. **Stock never goes negative**, preserving the existing domain invariant.

### 5.2 Event-by-event

| Event | Stock effect | Mechanism | Reference |
|---|---|---|---|
| Round released to kitchen | None | — | — |
| Item added (any state) | None | — | — |
| Item void, `KitchenStatus` null or `Pending` | None | — | — |
| Item void, `KitchenStatus` Acknowledged, Ready, or Served | **Waste** (qty = item quantity, only for `TrackInventory` products) | `DeductForWasteAsync` | `RestoOrderItem` id |
| Settlement | **Sale** for each non-voided `TrackInventory` item | `DeductForSaleAsync`, same transaction as the Sale | `Sale` id |
| Unpaid closure, per non-voided item with `KitchenStatus` ∈ {Acknowledged, Ready, Served} | **Waste** | `DeductForWasteAsync` | `RestoOrder` id |
| Unpaid closure, per item with `KitchenStatus` ∈ {null, Pending} | None | — | — |
| Cancel unsent | None (no Released round, so nothing was released) | — | — |
| Void of a settled Sale (paid void) | Restore via existing `VoidSaleService` (`ReverseForVoidAsync` for every `TrackInventory` line) | Unchanged | `Sale` id |

**Why `Pending` is not waste.** Pending means released but not yet acknowledged by the kitchen (design Section 6.2). Nothing has been started, so no product was consumed. This is a policy default — see decision D2 in Section 10.

### 5.3 Unpaid closure accounting

Unpaid closure creates **no** `Sale`, no `Payment`, no sale number, no cash movement, and does not touch the register session. Its only effects are:

1. Waste movements per Section 5.2, one per non-voided item in a released round with a consumed `KitchenStatus`, within the closure transaction.
2. `RestoOrder.Status = UnpaidClosed` with reason, actor, timestamp, and approver.
3. Table occupancy released automatically (the filtered unique index on `TableId` counts only `Open` orders).

Items remain on their rounds with their statuses. Nothing is deleted. Each waste movement carries the closure reason in its `reason` text, so the stock trail is self-explanatory without joining to the order.

### 5.4 Waste shortfall

`DeductForWasteAsync` is conditional like `DeductForSaleAsync`. If available stock is less than the waste quantity, the operation throws `InsufficientInventory` and the whole void or closure rolls back. Staff must correct stock first (an adjustment) and retry. This is the recommended default and keeps the non-negative invariant intact. The alternative (allow negative, or deduct partially) is decision D1 in Section 10.

### 5.5 Settlement retry and double-deduction

Settlement is one transaction containing the Sale, its items, payments, all `DeductForSaleAsync` calls, and `Order.Settle`. A retry after a lost response is resolved **before** any deduction by the idempotency check (Section 6.2): the existing Sale and order are returned and nothing is deducted again. A concurrent duplicate loses the `Sale.ClientRequestId` unique index and rolls back its own deductions with its transaction.

---

## 6. Lifecycle Services and Concurrency

### 6.1 Operations

| # | Operation | Locks | Writes | Stock |
|---|---|---|---|---|
| O1 | Open order | none (registers session read only) | `RestoOrder` | none |
| O2 | Add round | order | `RestoOrderRound` | none |
| O3 | Add item (with modifiers, discount) | order | `RestoOrderItem`, `RestoOrderItemModifier` | none |
| O4 | Release round | order (user path) or status guard only (system path, Section 6.4) | round, items (`KitchenStatus = Pending`) | none |
| O5 | Void item | order, then conditional item update (G5) | item | waste (Section 5.2) |
| O6 | Cancel unsent | order | order | none |
| O7 | Unpaid closure | order | order, items (no change), stock movements | waste (Section 5.2) |
| O8 | Settle | **session, then order** | Sale, SaleItems, SaleItemModifiers, Payments, order, stock movements | sale (Section 5.2) |
| O9 | KDS ack / ready / served | none (single-row conditional update) | item | none |

**Lock ordering invariant:** the session lock is always acquired **before** the order lock, and no operation acquires an order lock and then a session lock. Only O8 touches both. The graph is acyclic.

### 6.2 Settlement (O8) — steps

Inside the service, in order:

1. **Idempotency pre-check** (no lock): if `RestoOrder.SettlementRequestId == request.Key` (or `Sale.ClientRequestId == request.Key`), return the existing settlement result. If the order is `Settled` with a *different* key, reject `RestoOrderAlreadySettled` (409) with the existing `SaleId`.
2. Validate branch (404 for foreign branch), caller's open register session on the order's branch, payment inputs shape.
3. **Session lock**: `SELECT 1 FROM RegisterSessions WITH (UPDLOCK, HOLDLOCK) WHERE Id = @callerSessionId`. Re-check the session is `Open` and owned by the caller.
4. **Order lock**: `SELECT ... FROM RestoOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = @orderId AND TenantId = @tenant`.
5. Re-check: order `Open`; `RowVersion` equals the client's expected value (hard conflict on mismatch, Section 6.3); no Draft round with non-voided items (BillOut); at least one non-voided item.
6. Build the Sale from non-voided items (Section 4.2). `ResolvePayments` (shared, Section 6.5) against `grandTotal`.
7. `DocumentNumberService.NextAsync(...)` inside the transaction (same as checkout).
8. `Sale.Begin(..., clientRequestId: request.Key, origin: SaleOrigin.Resto)`, add items (`UnitPrice = item.UnitPriceSnapshot`, `CostPrice = item.CostPriceSnapshot`, `DiscountType/Value` from item), add `SaleItemModifier` rows, add payments, `Sale.Complete(...)`.
9. `SaveChangesAsync`. A `ClientRequestId` unique violation rolls back and returns the winner (checkout's exact pattern).
10. For each `TrackInventory` item: `DeductForSaleAsync`.
11. `RestoOrder.Settle(saleId, now)`; set `SettlementRequestId = request.Key`; `SaveChangesAsync`; commit.
12. Return the Sale result and, for PAYO, the round-release status (Section 6.4).

Important: the **session lock comes first** so that settlement serializes against session close and void. A close that runs first sees either the settled Sale or no Sale, never a half-written one, matching the existing void-vs-close guarantee.

### 6.3 Concurrency

- **Structural writes (O2–O7):** take the order lock first, then operate. The client must send the last-seen `RestoOrder.RowVersion`; the server compares it **after** acquiring the lock, and returns a hard conflict (`RestoOrderConcurrencyConflict`, 409) on mismatch. No silent overwrite.
- **System writes (O4 system path):** no client version. Protected by a conditional status guard (`UPDATE ... WHERE Status = Draft`), which is idempotent under concurrent workers without a lock (Section 6.4).
- **Void item (O5) vs KDS transition (O9)** (G5): O5 reads `KitchenStatus` under the order lock, then performs `UPDATE RestoOrderItems SET VoidedAtUtc = @now, ... WHERE Id = @id AND VoidedAtUtc IS NULL AND KitchenStatus IS NOT DISTINCT FROM @observed`. Zero rows affected means the kitchen moved the item first; O5 re-reads and re-decides waste. This prevents charging "no waste" for an item the kitchen had just started.
- **KDS transitions (O9)** are `UPDATE ... WHERE Id = @id AND KitchenStatus = @expected AND VoidedAtUtc IS NULL AND <order status ∈ {Open, Settled}>`. They never take the order lock. Once an order is `UnpaidClosed` or `Cancelled`, KDS transitions are refused by the subquery.
- **Settlement (O8) vs add item / void item (O3, O5):** serialized by the order lock; the settlement re-read happens after the lock, so an item added or voided concurrently is either fully included or fully excluded.

### 6.4 PAYO release: durable recovery contract

PAYO release is a separate, idempotent operation after settlement.

- **Operation:** `ReleaseRound(orderId, roundId, actor)`. Guard: round `Draft`, order `Settled` (PAYO) or `Open` (BillOut). Transition: `Released`, items to `Pending`. Replay on a `Released` round is a no-op and preserves the original releaser.
- **Durable state that defines "pending release":** a `Settled` PAYO order whose single round is still `Draft`, with a non-voided item. No additional column is required.
- **Contract for M3 (not implemented in M2):**
  - `ListPendingPayoReleases(tenant, branch?, olderThanUtc)`: returns settled PAYO orders with a Draft round, ordered by `SettledAtUtc`, for the reconciliation worker and the manual recovery action.
  - `ReleaseRound` as above, called by the worker and the manual button. Safe to call from any number of app instances; exactly one `UPDATE` wins.
  - Both paths are status-guarded and need no client `RowVersion` (a server process cannot supply one). The rule in Section 6.3 applies: client-driven structural writes require `RowVersion`; system release does not.
- **M2 delivers:** `ReleaseRound` (both paths share the same method) and `ListPendingPayoReleases` (query only). M3 adds the worker and the manual button.

### 6.5 Shared extractions (behavior-preserving)

Checkout must not change. Two pieces move out of `CheckoutService` so settlement reuses them:

1. **`SaleTotals.Compute(lines, pricesIncludeTax, deliveryCharge)`**: the four sum-and-combine expressions from Section 4.2. Checkout calls it with its existing inputs; the existing checkout tests guard the refactor.
2. **`PaymentResolver.Resolve(inputs, grandTotal)`**: moved from `CheckoutService.ResolvePayments`. Same exceptions and messages.

Both are pure functions with no database access, so they are unit-testable in isolation.

---

## 7. Return Prohibition

`ReturnService.CreateReturnAsync`'s eligibility check gains a condition: `sale.Origin == SaleOrigin.Resto` throws `BusinessRuleException(ErrorCodes.ReturnNotAllowedForResto, ...)`. This is the single server-side enforcement point. The frontend hides Return for Resto-origin sales as defense in depth. **Void requires no code change.**

M2 must include this change. Settlement is the first code path that creates Resto sales, so the prohibition must ship in the same milestone.

Test: a settled Resto sale is returned by an Owner; the server rejects with `ReturnNotAllowedForResto`. A retail sale is still returnable.

---

## 8. Permissions, Authorization, and Branch Isolation

### 8.1 New permissions

Each action has its own `UserPermission` member (codebase convention). Existing values run through 6 (`CashMovement`).

| Value | Member | Governs | Cashier rule |
|---|---|---|---|
| 7 | `RestoItemVoid` | Voiding an item that has been released (`KitchenStatus` not null) | Grant or Manager/Admin/Owner approval |
| 8 | `RestoOrderCancel` | Cancelling an unsent order | Grant or approval |
| 9 | `RestoUnpaidClose` | Unpaid closure of a released order | Grant or approval |

Voiding a **Draft** (never released) item needs no special permission for any POS role. Discount authorization reuses `DiscountApply` (Section 8.3).

Owner/Admin/Manager act directly on all three. Approval uses `VoidSaleApprovalInput` and `IApproverVerificationService`, branch-scoped.

### 8.2 Resolvers

Three new resolvers, each mirroring `FulfillmentCancelAuthorizationResolver` exactly, each with its own error code (`RestoItemVoidApprovalRequired`, `RestoOrderCancelApprovalRequired`, `RestoUnpaidCloseApprovalRequired`). Per codebase precedent, they are not shared.

### 8.3 Discounts

Line discounts on Resto items use the **same rule as checkout**: Owner/Admin/Manager direct; Cashier needs `DiscountApply` or approval. The approver is stored on the item (`DiscountApprovedByUserId`). Discounts are set at add-time only. To change a discount, void the item and add a new one. Settlement does not re-authorize discounts; authorization is frozen with the item.

### 8.4 Who may operate an order

- Opening, adding items, adding rounds, releasing rounds, and settling require the caller to hold an **open register session on the order's branch**. This mirrors checkout.
- Operations on an existing order are scoped to its `BranchId`. A branch-scoped user acting on another branch's order receives 404 (not 403), matching `VoidSaleService`.
- Owner/Admin see all branches; Manager and Cashier are branch-scoped by `IBranchAccessResolver`.

### 8.5 KitchenStaff

Role-level capability for KDS transitions (M5). No behavior in M2. `UserPermissionGrantService` continues to restrict grant targets to Cashier; no change needed in M2.

---

## 9. Testing Requirements

M2 acceptance tests, grouped by the risk they cover. Each is an integration test against a real tenant database unless marked unit.

**Settlement and retries**
- Settle, then re-send the identical request: same `SaleId`, no second Sale, no second deduction, same sale number.
- Settle, lose the response (simulated by discarding the client result), retry with the same key: resolves to the original Sale.
- Two concurrent settlements with the same key: exactly one Sale; the loser returns the winner.
- Two concurrent settlements with different keys on the same order: exactly one wins; the other gets `RestoOrderAlreadySettled` with the winning `SaleId`.

**Frozen totals and reconciliation**
- The displayed order summary equals the settled `Sale.GrandTotal` byte-for-byte for a mixed order (tax-exclusive and tax-inclusive variants, discounted and not, with modifiers).
- Changing the tenant's `PricesIncludeTax` or `TaxRatePercent` after open does not change the settled totals.
- Changing a catalog price after add does not change the settled totals.
- A modifier added before release is included; a modifier after release is rejected.

**Multi-round billing**
- Bill-Out with three rounds settles into one Sale containing all non-voided items across rounds.
- A Draft round with items blocks Bill-Out settlement; the same order settles after the round is released or voided.

**Voids**
- Void a Draft item: no stock change, no waste row.
- Void a Pending released item: no stock change.
- Void an Acknowledged, Ready, or Served item: one waste row per item; stock down by the item quantity.
- Void conditional-update race: a KDS transition races a void; exactly one wins, and waste is decided from the state that actually won.
- Void with insufficient stock for waste: rejected with `InsufficientInventory`; the item is not voided.

**Unpaid closure**
- Closure with mixed Pending/Ready items: waste rows only for Ready items; no Sale, no Payment, no sale number consumed, session untouched.
- Closure releases table occupancy: a new Open order can be opened on the same table immediately after.
- Closure with zero Released rounds: rejected (use cancel instead).
- Closure is idempotent on a repeated key; a different key on a closed order is rejected.

**Stock accounting and void compatibility**
- Settle a Resto order, then void the Sale through the **unmodified** `VoidSaleService`: stock returns to its pre-settlement level exactly (the M2 acceptance criterion from Section 11).
- Settled sale void does not restore waste from earlier voided items (no double restore).
- Stock is never negative after any sequence of the above.

**Occupancy**
- Two concurrent Open BillOut orders on one table: exactly one succeeds (DB filtered unique index).
- Settle and unpaid closure both release the table; cancel releases it too.

**Return prohibition**
- Return against a settled Resto sale is rejected with `ReturnNotAllowedForResto`; retail return unchanged.

**Authorization**
- Cashier without grant: void of a released item, cancel, unpaid closure, and discount each require approval; valid manager credentials succeed; invalid credentials fail; a Manager from another branch fails.
- Cashier with the matching grant acts directly.
- Owner acts directly on all.

**Tenant and branch isolation**
- Cross-tenant order id returns 404.
- Cross-branch order id returns 404 for a branch-scoped user.
- A PAYO release query returns only the calling tenant's (and, for branch-scoped callers, branch's) orders.

**Branch flags**
- Opening a PAYO order on a branch with `SupportsPayAsYouOrder = false` is rejected.
- Changing the flag after open does not affect an already-open order.

**Pure unit tests**
- `SaleTotals.Compute` and `PaymentResolver.Resolve` in isolation, including all Checkout exception paths.
- The waste policy function (`KitchenStatus` → waste yes/no).
- Every row of the transition table in Section 3.1, both legal and illegal.

**Checkout regression**
- The entire existing checkout suite must pass unchanged after the Section 6.5 extraction.

---

## 10. Product Decisions

### 10.1 Resolved (approved, or resolved from requirements and code)

| # | Decision | Resolution |
|---|---|---|
| R1 | Bill-level discounts | Deferred. Line-level only. |
| R2 | Partial-quantity voids | Deferred. Whole-line only. |
| R3 | Modifier summaries | Persisted on `SaleItem` as `SaleItemModifiers` rows (Section 3.5). |
| R4 | Walkouts after release | Explicit unpaid closure (Section 5.3). No automatic ₱0 Sale. |
| R5 | Return on Resto | Prohibited server-side, hidden in UI (Section 7). |
| R6 | Draft rounds before Bill-Out settlement | Must be released or voided first. Settlement is blocked otherwise. (Resolved from the lifecycle rules: charging for items the kitchen never saw is not an acceptable state.) |
| R7 | Settlement session | The caller's own open session on the order's branch, not the session the order was opened in (which may be closed). Mirrors checkout. |
| R8 | Cancel of an order with a released round | Not allowed; use unpaid closure. Already enforced in M1's `RestoOrder.Cancel`. |
| R9 | Voided settled Resto sale | The order stays `Settled`. Void does not reopen the order. Food was served; a new order is required. |
| R10 | Stock on unpaid closure | Waste for consumed items (Section 5.3). |
| R11 | Stock on Pending item void | No movement (policy default, see D2). |

### 10.2 Remaining decisions (cannot be resolved from approved requirements or existing code)

| # | Question | Recommended default | Why it needs sign-off |
|---|---|---|---|
| **D1** | Waste with insufficient stock: block the void/closure (`InsufficientInventory`), allow negative stock, or deduct partially and record the shortfall? | **Block.** Staff correct stock first. | Negative stock breaks the existing domain invariant and reporting assumptions. Partial deduction makes stock diverge from reality silently. Blocking can frustrate a real walkout. Requires an operational answer. |
| **D2** | Is a `Pending` (released but not acknowledged) item consumed food? | **No** (no waste). | Nothing has been started, so no product was used. But a kitchen may have begun prep before the status changed. Business call. |
| **D3** | Unpaid closure permission: Cashier needs grant or approval (as proposed), or Cashier can close directly? | **Grant or approval** (as proposed). | Walkouts are a loss event; the proposal treats them like voids. Business policy. |
| **D4** | Retail checkout has no register-session lock (Section 2.1). Fix in M2, or separately? | **Separately** (out of M2 scope). | Changing checkout's locking affects the most heavily used path in the app. Needs its own test plan and risk review. Pre-existing risk. |
| **D5** | Modifier itemization on the printed receipt: show modifier lines, or only the item name with a price that includes modifier deltas? | **Show modifier lines** (once `SaleItemModifiers` exists). | `SaleItem` now carries the data; receipt rendering is M6. Confirm before M6. |
| **D6** | Partial-quantity voids (deferred, R2). Revisit after real usage? | Revisit after pilot use. | Product judgement about whether void-and-re-add is acceptable in practice. |
| **D7** | `TrackInventory` toggled on a product between settlement and void: the void reverses stock the sale never deducted. Pre-existing in Retail too. Fix by snapshotting `TrackInventory` on `SaleItem`? | Snapshot in a later change, not M2. | Pre-existing; affects Retail and Resto alike. Needs a decision on migration of existing rows. |
| **D8** | Discount on a released item: allow changing it (requires re-authorization), or only void-and-re-add? | **Void-and-re-add** (as proposed, Section 8.3). | Simplicity and frozen-authorization guarantee vs. convenience. |

### 10.3 Items from earlier sections, still open

- Bill-level discount (R1) and its consumer trace (reports, receipts, tax export) remains a separate design pass.
- Waste cost ledger: the spec continues to record waste as audited events with no cost ledger. Quantifying waste cost is a later reporting feature.

---

## 11. Milestones and Acceptance Criteria

**M1 — Domain + migrations.** *Complete.*

**M2 — Order lifecycle services (application layer and its tests).**
Scope: operations O1–O8 (Section 6.1); O9 contract only (no endpoint); `ReleaseRound` and `ListPendingPayoReleases` (Section 6.4); the Return prohibition (Section 7); the shared extractions (Section 6.5); the three new permissions and resolvers (Section 8); additive migration for Section 3 fields and the `SaleItemModifiers` table.
Out of scope: controllers and endpoints beyond what is needed for tests, the KDS UI, the background worker, the manual release button, restaurant reports, receipt modifier rendering, frontend.
*Acceptance:* every test in Section 9 passes; the existing checkout suite passes unchanged; the void-compatibility test proves stock returns exactly to its pre-settlement level using the unmodified `VoidSaleService`; a settled Resto sale is not returnable; migrations apply cleanly against the current schema.

**M3 — Reliability and reconciliation.** Background worker (tenant-enumerating, per Section 6.4), manual release action, unacknowledged-ticket alert. Consumes `ListPendingPayoReleases` and `ReleaseRound`.

**M4 — Authorization hardening.** Endpoint-level policies for the three permissions and branch-flag enforcement at the API boundary. Service-level enforcement is already in M2.

**M5 — KDS.** Polling read API, station filtering, O9 endpoints with the conditional-update guard from Section 6.3.

**M6 — Reporting and receipts.** Additive Resto report methods; test proving the existing KPI reports include Resto sales unchanged; receipt modifier rendering per D5.

**M7 — Frontend.** Menu and modifier picker, order taking and table list, bill and settlement screen, unpaid-closure screen, KDS view, Return hidden for Resto sales, Staff Permissions additions for the three new permissions.

---

## 12. Dependencies for later milestones

- **M3** depends on `ReleaseRound` idempotency (Section 6.4) and `ListPendingPayoReleases` (query, M2).
- **M5** depends on the O9 conditional-update contract and the order-status subquery (Section 6.3).
- **M6** depends on `SaleItemModifiers` (M2) and D5.
- **M7** depends on all service contracts above and on the Return prohibition.
