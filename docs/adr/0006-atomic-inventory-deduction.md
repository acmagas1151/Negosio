# ADR 0006 — Atomic conditional inventory deduction for POS checkout

- Status: Accepted
- Date: 2026-08-29
- Phase: 3 (Retail POS)

## Context

Two cashiers sell the last unit of a product at the same moment. A naive "read quantity → check →
write" oversells and drives stock negative. Phase 2's management adjustment path
(`InventoryService.AdjustAsync`) solves concurrency with an EF `rowversion` token supplied by the
UI — but a POS checkout has no such token to round-trip, and forcing one would slow the till and
add a failure mode cashiers can't act on.

## Decision

`IInventoryPosting.DeductForSaleAsync` (called by `CheckoutService` inside the checkout transaction)
performs a **single atomic conditional UPDATE**:

```sql
UPDATE BranchInventories
SET QuantityOnHand = QuantityOnHand - @qty, UpdatedAtUtc = @now
WHERE TenantId = @t AND BranchId = @b AND ProductVariantId = @v
  AND QuantityOnHand >= @qty;
```

(implemented with EF Core `ExecuteUpdateAsync`, which runs on the ambient transaction). If it
affects **0 rows** — the row is missing or has less than `@qty` — checkout throws
`409 INSUFFICIENT_INVENTORY` and the whole transaction rolls back.

SQL Server evaluates the `>=` predicate and the decrement under one row lock, held to end of
transaction. A concurrent checkout for the same item blocks, then re-evaluates the predicate after
the first commits; if stock is now short it gets 0 rows and 409. Overselling is impossible and the
guarantee is explicit in one statement.

`QuantityBefore` / `QuantityAfter` for the `StockMovement.Sale` are read back inside the same
transaction (the row is write-locked, so `before = after + qty` is exact). The movement insert is
part of the same transaction — quantity never changes without a ledger row.

Returns use the same method without the `>=` predicate (`RestockForReturnAsync`,
`StockMovementType.Return`, `+qty`). Non-inventory-tracked products skip deduction entirely.

## Consequences

- No UI concurrency token for POS; the till stays fast.
- Exactly one of two racing final-item checkouts succeeds; stock lands at 0, never −1; the ledger
  has exactly one `Sale` movement.
- `InventoryService.AdjustAsync` (Phase 2, token-based) is untouched and still used by the
  management inventory screen.
