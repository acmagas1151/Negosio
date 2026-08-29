# ADR 0007 — Sale / document numbering strategy

- Status: Accepted
- Date: 2026-08-29
- Phase: 3 (Retail POS)

## Context

Every sale needs a human-readable receipt number; GUIDs are not acceptable on a receipt.
`COUNT(*) + 1` is unsafe — concurrent checkouts produce duplicates. A single global SQL `SEQUENCE`
is safe but gives each tenant a gappy stream. Tenants (and branches) expect their own tidy running
numbers, and later phases (purchase orders, stock transfers) need the same facility.

## Decision

A generic counter table **`DocumentNumberCounters`** keyed by `(TenantId, BranchId, Type)` (unique
index), where `Type` is `DocumentNumberType { Sale, Return, PurchaseOrder, StockTransfer }`.

`DocumentNumberService.NextAsync(tenantId, branchId, type, branchCode)` runs **inside the caller's
transaction**:

```sql
UPDATE DocumentNumberCounters
SET LastNumber = LastNumber + 1
OUTPUT INSERTED.LastNumber AS Value
WHERE TenantId = @t AND BranchId = @b AND [Type] = @type;
```

The single-row `UPDATE ... OUTPUT` is concurrency-safe: SQL Server takes the row lock, increments,
returns the new value, and holds the lock to end of transaction. If no counter row exists yet, the
service inserts one at `LastNumber = 0` (catching the unique-violation race) and retries the
`UPDATE`.

Numbers are formatted with a prefix and the branch code:

```
INV-MAIN-000001    (Sale)
RET-MAIN-000001    (Return)
```

`Sale` also carries a unique index `(TenantId, SaleNumber)` as defence-in-depth; the branch code in
the string keeps cross-branch numbers distinct.

## Consequences

- Contiguous, branch-scoped, human-readable numbers per document type.
- Committed numbers are **never reused** — a later void or refund does not roll the counter back.
- Allocation adds a brief per-(tenant, branch, type) row lock to each checkout; negligible for
  Phase 3 throughput, and it only serialises documents of the same type on the same till line.
- The same table serves future purchasing / transfer numbering with no schema change.
