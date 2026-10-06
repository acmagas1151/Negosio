# RestoPOS M2 — Order Lifecycle Services Implementation Plan

> **Status: PLAN ONLY.** No feature code or migrations have been written. Execute only after the design spec's Section 10.2 decisions are signed off (or their recommended defaults explicitly accepted).

**Goal:** Implement the application services for the RestoPOS order lifecycle (open, rounds, items with modifiers and discounts, release, void, cancel, unpaid closure, settlement) and the Return prohibition for Resto sales, with full integration tests.

**Architecture:** Application services in a new `Negosio.Application/Resto` folder, following the existing service/resolver/contracts/validators layout. Domain changes are additive. Settlement reuses checkout's numbering, payments, totals, and inventory pipeline through two behavior-preserving extractions. No endpoints beyond test needs; controllers belong to M4/M5.

**Tech Stack:** .NET 9, EF Core 9, SQL Server (LocalDB), xUnit, FluentAssertions, FluentValidation.

**Spec:** `docs/superpowers/specs/2026-09-28-restopos-design.md` (Sections 3–9 are the source of truth for every rule in this plan).

## Global Constraints

- Every new entity and column is `TenantId`-scoped. Every query filters by `TenantId`.
- Domain invariant violations throw BCL exceptions. Service-layer rules throw `BusinessRuleException`/`ConflictException`/`ForbiddenAppException`/`NotFoundException` with `ErrorCodes`.
- Lock ordering is absolute: **session lock before order lock; never order then session.** Only Settlement touches both.
- `RestoOrder` writes by a client require the expected `RowVersion` (checked after the lock). System release does not (status guard only).
- Stock never goes negative. Waste and sale deductions are conditional.
- Checkout's observable behavior must not change. The full existing checkout suite must pass unchanged after Task 2.
- Migrations: M1's `AddRestoPos` is already on `master` and must **not** be edited. All M2 schema changes go into a new migration (Task 4).
- Namespaces are flat per codebase convention (`Negosio.Domain.Entities`, never nested by folder).
- No commits, merges, pushes, or branch deletions without explicit instruction.

---

## Dependency graph

```
T1 (enums, permissions, error codes, staff plumbing)
 ├─► T3 (domain amendments) ─► T4 (migration 2) ─┐
 ├─► T6 (authorization resolvers) ───────────────┤
 ├─► T9 (Return prohibition)                      │
T2 (SaleTotals + PaymentResolver extraction) ────┤
T5 (DeductForWasteAsync) ────────────────────────┤
                                                 ▼
                    T7 (order service: open, rounds, items, release, void, cancel, PAYO query)
                                                 ▼
                    T8 (settlement + unpaid closure)
                                                 ▼
                    T10 (integration & concurrency suite)
                                                 ▼
                    T11 (full verification, handoff)
```

T1, T2, T5, T9 may run in parallel after nothing. T3 needs T1. T4 needs T3. T6 needs T1. T7 needs T2, T3, T5, T6. T8 needs T7. T10 needs T7, T8.

---

### Task 1: Enum values, permissions, error codes, staff permission plumbing

**Files:**
- Modify: `src/Negosio.Domain/Enums/UserPermission.cs` — add `RestoItemVoid = 7`, `RestoOrderCancel = 8`, `RestoUnpaidClose = 9`.
- Modify: `src/Negosio.Domain/Enums/RestoOrderStatus.cs` — add `UnpaidClosed = 4`.
- Modify: `src/Negosio.Application/Common/ErrorCodes.cs` — add the codes listed below.
- Modify: `src/Negosio.Application/Staff/StaffContracts.cs` — `StaffMemberDto` and `ChangeStaffPermissionsRequest` gain three bools, **with default `false`** so future permissions don't churn every call site.
- Modify: `src/Negosio.Application/Staff/StaffService.cs` — three `StaffMemberDto` construction sites.
- Modify: `src/Negosio.Application/Staff/UserPermissionGrantService.cs` — mapping.
- Modify: `src/Negosio.Api/Controllers/StaffController.cs` — dictionary entries.
- Modify (test churn, minimal): existing `ChangeStaffPermissionsRequest(...)` call sites rely on the new defaults and need no edit.

**Error codes to add:**
`RestoItemVoidApprovalRequired`, `RestoOrderCancelApprovalRequired`, `RestoUnpaidCloseApprovalRequired`, `ReturnNotAllowedForResto`, `RestoOrderAlreadySettled`, `RestoOrderConcurrencyConflict`, `RestoOrderNotOpen`, `RestoDraftRoundsPending`, `RestoRoundNotDraft`, `RestoServiceTypeNotEnabled`, `RestoTableRequired`, `RestoTableNotFound`, `RestoTableOccupied` (informational; DB index is backstop), `RestoOrderCancelHasReleasedRound`, `RestoUnpaidCloseRequiresReleasedRound`, `RestoItemAlreadyVoided`, `RestoItemNotFound`, `RestoRoundNotFound`, `RestoOrderNotFound`, `RestoNoBillableItems`, `RestoSessionRequired`, `RestoModifierNotAllowedAfterRelease`, `RestoModifierOptionNotFound`, `RestoStationNotFound`, `RestoStationInactive`, `RestoProductNotOrderable`.

**Acceptance:**
- Build passes. Existing 585 tests pass with no edits beyond compile fixes.
- Unit test: enum integer values are stable (`UserPermission.RestoItemVoid == 7`, etc.), guarding persistence.
- Unit test: `ChangeStaffPermissionsRequest` constructed with the original five arguments still compiles and defaults the new three to `false`.

---

### Task 2: Extract `SaleTotals` and `PaymentResolver` from checkout (behavior-preserving)

**Files:**
- Create: `src/Negosio.Application/Pos/SaleTotals.cs` — `static Totals Compute(IReadOnlyList<LineAmounts> lines, bool pricesIncludeTax, decimal deliveryCharge)` returning `subtotal, discountTotal, taxTotal, saleTotal, grandTotal`. Formula exactly as in spec Section 4.2 and today's `CheckoutService` lines 157–164.
- Create: `src/Negosio.Application/Pos/PaymentResolver.cs` — `static List<ResolvedPayment> Resolve(IReadOnlyList<CheckoutPaymentInput> inputs, decimal grandTotal)`. Moved verbatim from `CheckoutService.ResolvePayments`, including the `ResolvedPayment` record.
- Modify: `src/Negosio.Application/Pos/CheckoutService.cs` — call both; delete the private `ResolvePayments` and the inline totals block.

**Tests:**
- Create: `tests/Negosio.UnitTests/Pos/SaleTotalsTests.cs` — tax-exclusive, tax-inclusive, discounted, delivery charge included, zero-tax.
- Create: `tests/Negosio.UnitTests/Pos/PaymentResolverTests.cs` — cash change, cash partial, non-cash overpay clamp, insufficient throws `PaymentInsufficient`, zero/negative throws `InvalidPayment`, missing amount throws.

**Acceptance:**
- Entire existing checkout suite passes **unchanged** (no test edits).
- New unit tests pass.
- `CheckoutService` diff is limited to call-site replacement (review by diff, not by reading the new code alone).

---

### Task 3: Domain amendments and the `SaleItemModifier` entity

**Files:**
- Modify: `src/Negosio.Domain/Entities/Resto/RestoOrder.cs`
  - `OpenPayAsYouOrder` and `OpenBillOut` gain `bool pricesIncludeTaxSnapshot`.
  - Add properties: `PricesIncludeTaxSnapshot`, `SettlementRequestId`, `UnpaidClosedAtUtc`, `UnpaidClosedByUserId`, `UnpaidClosureReason`, `UnpaidClosureApprovedByUserId`, `UnpaidClosureRequestId`.
  - `Settle(Guid saleId, Guid settlementRequestId, DateTime nowUtc)` replaces the M1 signature; sets `SettlementRequestId`.
  - New `UnpaidClose(Guid closedByUserId, string reason, Guid? approvedByUserId, Guid closureRequestId, DateTime nowUtc)`: requires `Open`, `ServiceType == BillOut`, ≥1 `Released` round; sets `UnpaidClosed`.
- Modify: `src/Negosio.Domain/Entities/Resto/RestoOrderItem.cs`
  - Add `DiscountKind`, `DiscountValue`, `DiscountApprovedByUserId`, `CostPriceSnapshot`. Factory parameters added; all existing argument-count call sites updated.
  - `Void` signature unchanged. The conditional-update semantics are enforced by the service (Task 7), not the domain.
- Create: `src/Negosio.Domain/Entities/Sales/SaleItemModifier.cs` — `SaleItemModifier` with snapshot fields and an `internal` factory.
- Modify: `src/Negosio.Domain/Entities/Sales/SaleItem.cs` — `internal` `AddModifier(...)` method and `Modifiers` collection (private backing list, field access mode). No change to existing constructor signature.
- Modify configs: `RestoOrderConfiguration.cs`, `RestoOrderItemConfiguration.cs`, `SaleItemConfiguration.cs` (navigation to modifiers).
- Create config: `src/Negosio.Infrastructure/Persistence/Configurations/SaleItemModifierConfiguration.cs` — table `SaleItemModifiers`, FK to `SaleItems` with **cascade**, index `(TenantId, SaleItemId)`.

**Tests (unit):**
- `tests/Negosio.UnitTests/Resto/RestoOrderLifecycleTests.cs` — every legal and illegal row of spec Section 3.1's transition table that is a domain-level check (`Settle` on non-Open; `UnpaidClose` on PAYO; `UnpaidClose` with zero Released rounds; `Cancel` with a Released round).
- `tests/Negosio.UnitTests/Sales/SaleItemModifierTests.cs` — snapshot fields, non-negative delta.
- Update existing M1 unit tests (`RestoOrderTests`, `RestoOrderItemTests`, `RestoOrderRoundTests`, `RestoTableTests`, `RestoStationTests`, `ModifierGroupTests`, `RestoOrderItemModifierTests`) for the changed factory signatures. Adjust only signatures; do not weaken assertions.

**Acceptance:**
- Build and unit suite pass.
- No behavioral change to any M1 transition; the only semantic addition is the three new `Settle`/`UnpaidClose` guards.

---

### Task 4: Migration 2 (`AddRestoM2Lifecycle`)

**Generate with:** `dotnet ef migrations add AddRestoM2Lifecycle --context TenantDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api --output-dir Persistence/Migrations/Tenant`, after `dotnet tool restore`. Stop any running API first (file lock).

**Expected schema changes:**
- `RestoOrders`: `PricesIncludeTaxSnapshot bit NOT NULL DEFAULT 0`; `UnpaidClosedAtUtc datetime2 NULL`; `UnpaidClosedByUserId uniqueidentifier NULL`; `UnpaidClosureReason nvarchar(500) NULL`; `UnpaidClosureApprovedByUserId uniqueidentifier NULL`; `UnpaidClosureRequestId uniqueidentifier NULL`; `SettlementRequestId uniqueidentifier NULL`. Filtered unique indexes: `(TenantId, SettlementRequestId) WHERE SettlementRequestId IS NOT NULL` and `(TenantId, UnpaidClosureRequestId) WHERE UnpaidClosureRequestId IS NOT NULL`.
- `RestoOrderItems`: `DiscountKind int NOT NULL DEFAULT 0`; `DiscountValue decimal(18,2) NOT NULL DEFAULT 0`; `DiscountApprovedByUserId uniqueidentifier NULL`; `CostPriceSnapshot decimal(18,2) NULL`.
- New table `SaleItemModifiers`: `Id`, `TenantId`, `SaleItemId` (FK, cascade), `ModifierGroupNameSnapshot nvarchar(100)`, `ModifierOptionNameSnapshot nvarchar(100)`, `PriceDeltaSnapshot decimal(18,2)`, `SortOrder int`, `CreatedAtUtc`, `UpdatedAtUtc`. Index `(TenantId, SaleItemId)`.

**Migration impact assessment (verify before running):**
- M1 never wrote Resto rows (no service exists), so `RestoOrders`/`RestoOrderItems` are empty in every tenant. The `DEFAULT 0` values are therefore inert. Verify with a `SELECT COUNT(*)` against a sampled tenant DB before merge.
- `SaleItemModifiers` is new; existing `SaleItems` rows gain nothing.
- Do **not** edit `AddRestoPos` (already on `master`).

**Test churn:** `TenantMigrationTests` hardcodes a pending-migration count after a rollback checkpoint (currently `HaveCount(5)`). Bump to 6 and update the comment.

**Acceptance:**
- Migration applies on a fresh tenant DB and on a copy of a tenant DB that has the M1 migration.
- Round trip (forward, back, forward) passes the existing `TenantMigrationTests` pattern.
- `dotnet ef migrations list` shows the new migration last and no pending model changes.

---

### Task 5: Waste inventory posting

**Files:**
- Modify: `src/Negosio.Application/Inventory/InventoryPosting.cs` — add to `IInventoryPosting`:
  `Task DeductForWasteAsync(Guid tenantId, Guid branchId, Guid productVariantId, decimal quantity, string referenceType, Guid referenceId, string reason, Guid userId, CancellationToken ct = default);`
  Implementation mirrors `DeductForSaleAsync` exactly: conditional `ExecuteUpdateAsync` with `QuantityOnHand >= quantity`; `ConflictException(InsufficientInventory)` on zero rows; one `StockMovement` with `StockMovementType.Waste`, negative quantity, `reason` set.

**Tests:**
- Create: `tests/Negosio.IntegrationTests/Inventory/WasteDeductionTests.cs`
  - Sufficient stock: quantity drops, one `Waste` movement with correct before/after, reference fields set.
  - Insufficient stock: `InsufficientInventory`, quantity unchanged, no movement row.
  - Missing inventory row: `InsufficientInventory` (never creates a row, unlike restock).

**Acceptance:** all three pass; existing inventory tests unchanged.

---

### Task 6: Authorization resolvers for the three Resto permissions

**Files:**
- Create: `src/Negosio.Application/Resto/RestoItemVoidAuthorizationResolver.cs`, `RestoOrderCancelAuthorizationResolver.cs`, `RestoUnpaidCloseAuthorizationResolver.cs`. Each: interface + sealed class, copy of `FulfillmentCancelAuthorizationResolver`'s shape. Role gate (Owner/Admin/Manager/Cashier); Owner/Admin/Manager direct; Cashier needs its own grant or approval via `IApproverVerificationService`; returns approver id or null.
- Modify: `src/Negosio.Application/DependencyInjection.cs` — register all three.

**Tests:**
- Create: `tests/Negosio.IntegrationTests/Resto/RestoAuthorizationTests.cs`: for each resolver — Owner direct; Cashier with grant direct; Cashier without grant gets its approval error code; valid manager credentials succeed (approver returned); invalid credentials throw; Manager from another branch throws `RestoApproverWrongBranch`-equivalent (reuse existing `VoidApproverWrongBranch` code, consistent with other resolvers).

**Acceptance:** all resolver tests pass; no change to existing resolvers.

---

### Task 7: Order service (open, rounds, items, modifiers, release, void, cancel, PAYO query)

**Files:**
- Create: `src/Negosio.Application/Resto/IRestoOrderService.cs` and `RestoOrderService.cs`.
- Create: `src/Negosio.Application/Resto/RestoOrderContracts.cs` — requests (`OpenRestoOrderRequest`, `AddRestoRoundRequest`, `AddRestoItemRequest` with `Modifiers` list and optional `Discount` + `Approval`, `VoidRestoItemRequest`, `CancelRestoOrderRequest`, `ReleaseRestoRoundRequest`), DTOs (`RestoOrderDto`, `RestoOrderSummaryDto` with the Section 4.2 totals), and `PendingPayoReleaseDto`.
- Create: `src/Negosio.Application/Resto/RestoOrderValidators.cs` — FluentValidation rules: names, quantities > 0, modifier counts, discount ranges (delegate final range checks to `SaleLineCalculator`), expected `RowVersion` present on structural writes.
- Create: `src/Negosio.Application/Resto/RestoPricing.cs` — builds an item's frozen amounts: `effectiveUnitPrice = variant.SellingPrice + Σ modifier delta`, then `SaleLineCalculator.Calculate(...)` with the order's `TaxRateSnapshot` and `PricesIncludeTaxSnapshot`. Also builds `RestoOrderSummaryDto` via `SaleTotals.Compute` (Task 2).
- Copy `EnsureDiscountAuthorized` from `CheckoutService` into `RestoOrderService` as a private method (codebase convention: per-service copy, not shared).

**Service rules:**
- **Open:** branch flag for the requested service type (`RestoServiceTypeNotEnabled`); caller's open session on the branch (`RestoSessionRequired`); BillOut requires a table in the same tenant/branch and `IsActive` (`RestoTableRequired`/`RestoTableNotFound`); freeze `PricesIncludeTaxSnapshot` from `TenantProfile`. Table occupancy is enforced by the DB index; translate that `DbUpdateException` to `RestoTableOccupied`.
- **Add round:** order lock; order `Open`; new round number via `OpenNextRound`.
- **Add item:** order lock; order `Open`; round `Draft`; variant active, product orderable (`RestoProductNotOrderable` if product not active); station active; modifiers resolved from `ModifierOption` where the option belongs to the tenant and is active; discount authorized per Section 8.3, approver stored; freeze all snapshots. Use `RestoPricing`.
- **Release round (user path):** order lock; `RowVersion` check; order status guard (BillOut `Open`; PAYO `Settled`); round `Draft` (idempotent no-op if `Released`).
- **Release round (system path, for M3):** no lock and no `RowVersion`; conditional `UPDATE RestoOrderRounds SET Status=Released … WHERE Id=@id AND Status=Draft` plus the order-status subquery; zero rows = already released or not eligible, which is success for idempotent callers.
- **Void item:** order lock; `RowVersion` check; item not voided. Read `KitchenStatus` (observed). Decide waste per spec Section 5.2. Perform the **conditional item update** `WHERE Id=@id AND VoidedAtUtc IS NULL AND KitchenStatus IS NOT DISTINCT FROM @observed` (spec G5). Zero rows → re-read and re-decide once, then fail with `RestoItemAlreadyVoided` if already voided. Authorization: Draft item → none; released item → `RestoItemVoid` resolver. Waste via `DeductForWasteAsync` (ref `RestoOrderItem`) in the same transaction, only for `TrackInventory` products. Stock effect ordering: waste before the item update so an `InsufficientInventory` rolls back the void.
- **Cancel unsent:** order lock; `RowVersion`; order `Open`; zero Released rounds (`RestoOrderCancelHasReleasedRound`); `RestoOrderCancel` resolver; `RestoOrder.Cancel`.
- **Summary:** `RestoOrderSummaryDto` over non-voided items with `SaleTotals`.
- **Query `ListPendingPayoReleases(tenantId, branchId?, olderThanUtc)`:** settled PAYO orders with a Draft round and ≥1 non-voided item.

**Tests:** written in Task 10 integration suite; this task includes the unit tests for `RestoPricing` (effective price with modifiers; tax-inclusive and exclusive; discount).

**Acceptance:** service compiles; unit tests for `RestoPricing` pass; each rule above has an integration test (Task 10).

---

### Task 8: Settlement and unpaid closure services

**Files:**
- Create: `src/Negosio.Application/Resto/IRestoSettlementService.cs` and `RestoSettlementService.cs`.
- Create (contracts in `RestoOrderContracts.cs`): `SettleRestoOrderRequest(byte[] ExpectedRowVersion, Guid SettlementRequestId, IReadOnlyList<CheckoutPaymentInput> Payments)`, `UnpaidCloseRestoOrderRequest(string Reason, Guid ClosureRequestId, VoidSaleApprovalInput? Approval, byte[] ExpectedRowVersion)`, results.

**Settlement (spec Section 6.2) — exact steps:**
1. Idempotency pre-check by `SettlementRequestId` (order) or `Sale.ClientRequestId`; return original; different key on settled order → `RestoOrderAlreadySettled` with `SaleId`.
2. Validate branch/session as in Section 8.4; session must be the caller's own open session on the order's branch.
3. Session lock `UPDLOCK,HOLDLOCK`; re-check session open and owned.
4. Order lock `UPDLOCK,HOLDLOCK` via a raw `SqlQuery` (same pattern as Void).
5. Re-check: order `Open`; `RowVersion` equals expected (`RestoOrderConcurrencyConflict`); no Draft round with non-voided items for BillOut (`RestoDraftRoundsPending`); ≥1 non-voided item (`RestoNoBillableItems`).
6. Build totals via `SaleTotals`; `PaymentResolver.Resolve`.
7. Transaction: `DocumentNumberService.NextAsync`; `Sale.Begin(..., clientRequestId: SettlementRequestId, origin: SaleOrigin.Resto)`; add items (`UnitPrice = UnitPriceSnapshot`, `CostPrice = CostPriceSnapshot`, `DiscountType/Value` from item, delivery/pickup quantities 0); add `SaleItemModifier` rows from item modifiers in `SortOrder` of creation; add payments; `Sale.Complete(...)`.
8. `SaveChangesAsync`, catching the `ClientRequestId` unique violation: rollback and return the winner (checkout pattern).
9. `DeductForSaleAsync` for each `TrackInventory` item.
10. `RestoOrder.Settle(sale.Id, SettlementRequestId, now)`; `SaveChangesAsync`; commit.
11. PAYO: return the round's status; the release is **not** called here (spec 6.4, separate operation). The caller may invoke `ReleaseRound` immediately. Recovery covers failure.

**Unpaid closure (spec Section 5.3) — exact steps:**
1. Idempotency by `UnpaidClosureRequestId` (same pattern).
2. Order lock; `RowVersion`; order `Open`; BillOut only; ≥1 Released round (`RestoUnpaidCloseRequiresReleasedRound`).
3. `RestoUnpaidClose` resolver (Section 8).
4. Transaction: for each non-voided item of a Released round: if `KitchenStatus ∈ {Acknowledged, Ready, Served}` and `TrackInventory` → `DeductForWasteAsync` (ref `RestoOrder`, reason text includes closure reason). Pending or null: no movement.
5. `RestoOrder.UnpaidClose(...)`; `SaveChangesAsync`; commit.
6. **No** Sale, Payment, sale number, or session interaction.

**Tests:** Task 10.

**Acceptance:** settlement and closure each pass their integration tests; zero Sale rows and zero `DocumentNumber` increments on closure (asserted).

---

### Task 9: Return prohibition for Resto sales

**Files:**
- Modify: `src/Negosio.Application/Sales/ReturnService.cs` — after the status check, `if (sale.Origin == SaleOrigin.Resto) throw new BusinessRuleException(ErrorCodes.ReturnNotAllowedForResto, "Returns are not supported for restaurant orders. Void the sale instead.");`
- No change to `VoidSaleService`.

**Tests:**
- Create: `tests/Negosio.IntegrationTests/Resto/RestoReturnProhibitionTests.cs` — create a Resto-origin sale directly via `Sale.Begin(..., SaleOrigin.Resto)` in test setup (settlement not required for this test); return is rejected; a retail sale remains returnable.

**Acceptance:** test passes; existing `ReturnTests` pass unchanged.

---

### Task 10: Integration and concurrency suite

**Files (create):**
- `tests/Negosio.IntegrationTests/Resto/RestoSettlementTests.cs` — spec Section 9 "Settlement and retries", "Frozen totals and reconciliation", "Multi-round billing".
- `tests/Negosio.IntegrationTests/Resto/RestoVoidTests.cs` — spec Section 9 "Voids", including the KDS-vs-void conditional race (two concurrent requests, one wins).
- `tests/Negosio.IntegrationTests/Resto/RestoUnpaidClosureTests.cs` — spec Section 9 "Unpaid closure".
- `tests/Negosio.IntegrationTests/Resto/RestoStockAccountingTests.cs` — spec Section 9 "Stock accounting and void compatibility". **Key test:** settle, void via unmodified `VoidSaleService`, assert `BranchInventory.QuantityOnHand` equals the pre-settlement value.
- `tests/Negosio.IntegrationTests/Resto/RestoOccupancyTests.cs` — concurrent BillOut opens on one table; table freed on settle, closure, cancel.
- `tests/Negosio.IntegrationTests/Resto/RestoIsolationTests.cs` — cross-tenant 404, cross-branch 404, PAYO query scoping.
- `tests/Negosio.IntegrationTests/Resto/RestoBranchFlagTests.cs` — flag enforcement at open; flag change after open.
- `tests/Negosio.UnitTests/Resto/PayoReleaseTests.cs` — system release idempotency (unit with fake context if practical; otherwise integration).

**Test-helper note:** settlement tests need a stocked product, an open register session, and modifier fixtures. Add fixture helpers to `IntegrationTest` base class (`CreateModifierGroupAsync`, `CreateRestoStationAsync`, `OpenBillOutOrderAsync`) rather than duplicating in each file.

**Acceptance:** every bullet in spec Section 9 maps to a named test. Run the concurrency tests at least 10 times each to confirm stability (these are race tests; a single green run is not evidence).

---

### Task 11: Full verification and handoff

- Run the full backend suite. Expected: all existing tests pass (checkout unchanged), plus the new suite. Record counts before and after.
- Run `tsc -b` and `npm run build` (no frontend changes in M2; confirms no contract drift).
- Confirm `dotnet ef migrations list` shows `AddRestoM2Lifecycle` last.
- Confirm no edits to `AddRestoPos` or any previously applied migration.
- Update `handover.md` and `negosio-status.md` per standing rules. Do **not** commit, push, or merge without explicit instruction.

---

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Checkout regression from Task 2 extraction | Task 2 is isolated and gated by the unchanged checkout suite before any Resto work starts. |
| Lock-order inversion introduced later | Lock order is stated as a Global Constraint and in spec 6.1; Task 10 includes a settle-vs-void-item race. |
| Stock divergence from waste or sale double-counting | Section 5.1's exactly-once model; Task 10 settle→void compatibility test and the no-double-restore test. |
| Frozen totals drift from a tenant tax change | `PricesIncludeTaxSnapshot` and `TaxRateSnapshot` frozen at add-time; Task 10 test with a tenant tax change between add and settle. |
| Retry creates a second Sale | Settlement key stored on the order and as `Sale.ClientRequestId`; double-guarded; Task 10 retry and concurrent-duplicate tests. |
| Table released while items still in kitchen | Occupancy releases only on `Settled`/`Cancelled`/`UnpaidClosed`, each of which requires zero unfinished kitchen work by its rules (closure accounts for it as waste). |
| Pre-existing Retail session-lock gap (spec D4) | Out of M2 scope; Resto settlement takes the lock correctly, so Resto sales are not affected by the gap. |

## Remaining open items for sign-off before execution

These are the spec's Section 10.2 decisions. Each has a recommended default that this plan implements. Confirm or change before Task 5 (waste) and Task 8 (closure):

- **D1** waste shortfall: block (plan default).
- **D2** Pending items are not waste (plan default).
- **D3** unpaid closure is grant-or-approval for Cashier (plan default).
- **D4** retail checkout lock gap deferred (plan default).
- **D7** `TrackInventory` snapshot deferred (plan default).
- **D8** discounts are set at add-time only (plan default).
