# ADR 0003 — SQL Server `rowversion` for inventory concurrency

- Status: Accepted
- Date: 2026-08-29
- Phase: 2 (Catalog + Inventory)

## Context

Two operators (or a POS terminal and a stock count) can adjust the same `BranchInventory` row at
nearly the same time. A last-write-wins update silently loses one adjustment and corrupts the
quantity. Phase 2 must reject the stale write, and it should be ready for retry/idempotency work in
the POS phase.

## Decision

`BranchInventory.RowVersion` is a SQL Server `rowversion` column configured as an EF Core
concurrency token (`.IsRowVersion()`). The adjustment API is optimistic **end to end**:

- `GET /api/inventory` and `GET /api/inventory/{id}` return `concurrencyToken` — the Base64 of the
  row's `RowVersion`.
- `POST /api/inventory/adjustments` accepts `expectedConcurrencyToken`.
  - **Opening stock** (no row exists yet): the token is not required and is ignored.
  - **Adjusting an existing row**: the token is **required**. Missing/blank or non-Base64 →
    `400 INVALID_INVENTORY_ADJUSTMENT`. The service sets it as the tracked entity's
    `RowVersion` *original value*, so `SaveChanges` emits `... WHERE RowVersion = @token`.
  - A `DbUpdateConcurrencyException` (0 rows affected) → the whole transaction rolls back and the
    API returns `409 INVENTORY_CONCURRENCY_CONFLICT` with a retry message. The quantity is never
    partially updated.
- Every successful adjustment returns the **new** token so a client can chain edits.

## Consequences

- No lost updates; the loser gets a clear 409 and can re-read and retry with a fresh token.
- The client is responsible for round-tripping the token (the React adjust dialog does this and, on
  409, refreshes the row rather than auto-retrying a user-entered amount).
- Concurrent *first* adjustments for the same `(branch, variant)` race on the unique index instead
  of the rowversion; that unique-violation is also mapped to `409 INVENTORY_CONCURRENCY_CONFLICT`.
