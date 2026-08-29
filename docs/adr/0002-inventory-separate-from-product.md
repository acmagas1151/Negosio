# ADR 0002 — Inventory is modelled separately from Product

- Status: Accepted
- Date: 2026-08-29
- Phase: 2 (Catalog + Inventory)

## Context

Quantity-on-hand is branch-specific and is a financial/operational source of truth. A shortcut such
as `Product.Quantity -= n` cannot represent per-branch stock, gives no audit trail, and invites
lost-update bugs.

## Decision

Two dedicated aggregates, neither of which hangs off `Product`:

- **`BranchInventory`** — `(TenantId, BranchId, ProductVariantId)` identity, `QuantityOnHand`,
  `ReorderLevel`, `RowVersion`. Unique index on `(TenantId, BranchId, ProductVariantId)` so a
  sellable item has exactly one inventory row per branch. It references **`ProductVariantId` only**
  — no denormalized `ProductId`. `?productId=` / `?categoryId=` filters resolve through
  `ProductVariant.ProductId` (served by `IX_ProductVariants_TenantId_ProductId`) via a join.
- **`StockMovement`** — append-only ledger. One row per change to `QuantityOnHand`, recording
  `Type`, signed `Quantity`, `QuantityBefore`, `QuantityAfter`, `Reason`, `CreatedByUserId`,
  `CreatedAtUtc`. Also references `ProductVariantId` only.

`QuantityOnHand` / `ReorderLevel` / movement quantities are `decimal(18,3)` — retail piece counts
fit exactly, and future weight/volume units (kg, L) work without a schema change (unit conversion
itself is out of scope for Phase 2). Money stays `decimal(18,2)`.

All stock changes go through one application service (`InventoryService.AdjustAsync`), inside a
database transaction that writes the `BranchInventory` update and the `StockMovement` together or
not at all. `QuantityOnHand` is never mutated without a movement.

## Consequences

- Every future stock-changing workflow (POS sale, transfer, purchase receipt, waste) reuses the
  same mutation path and ledger.
- Dropping the denormalized `ProductId` keeps consistency trivially correct at the cost of one join
  on product/category-filtered reads. Phase 2 movement volume is tiny (manual adjustments only). If
  a later, high-volume `Sale` phase needs the column, the documented path is a composite foreign key
  `StockMovement(ProductVariantId, ProductId) -> ProductVariant(Id, ProductId)` so the database, not
  the application, enforces the pair.
