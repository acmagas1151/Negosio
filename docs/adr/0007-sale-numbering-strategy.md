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

**Sales and returns share one _branch transaction sequence_.** Both allocate with
`DocumentNumberType.Sale`, so within a branch every completed sale and every return draws the next
number in a single monotonic stream (…a sale gets `00000004`, the next return gets `00000005`, and
so on). The `Return` counter type is retained for enum-value stability but is no longer used for
allocation. The `SaleNumber` / `ReturnNumber` property names are kept as-is — they now both hold a
value from the shared sequence, and a `SaleReturn` also echoes its originating sale's number
(`SaleReturnDto.OriginalSaleNumber`, joined at read time — no stored column).

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

Sale and return numbers are formatted as a **bare zero-padded 8-digit** running number:

```
00000001    (sale)
00000002    (sale)
00000003    (return, references sale 00000001)
```

Reserved future document types keep a prefixed, branch-scoped format (`PO-MAIN-000001`,
`TRN-MAIN-000001`). `Sale` carries a unique index `(TenantId, SaleNumber)` and `SaleReturn` a unique
`(TenantId, ReturnNumber)` as defence-in-depth; because both tables draw from the same per-branch
stream their strings never collide within a branch.

**Void is deferred.** No void numbering is implemented or reserved; a future void may keep the
original sale number rather than consume a new one.

### Migrating an existing deployment

This shared-sequence + bare-8-digit format was adopted before any production data existed, so the
codebase carries **no data migration**. A real deployment that already allocated numbers under the
previous scheme (`INV-<branch>-000001` from the `Sale` counter, `RET-<branch>-000001` from a
separate `Return` counter) would need a **one-time reconciliation**, run per tenant DB, roughly:

1. Decide whether to renumber history or leave old documents with their legacy strings (renumbering
   changes printed/exported receipts — usually leave history as-is).
2. Seed the shared `Sale` counter's `LastNumber` to `MAX(existing sale number, existing return
   number)` for each `(TenantId, BranchId)` so new documents continue past the highest used value.
3. Remove or ignore the now-unused `Return` counter rows.
4. Backfill any UI/read model that expects `OriginalSaleNumber` (here it is computed at read time,
   so nothing to backfill).

The developer tenant DB used during Phase 3 was reset instead (test sales/returns/movements wiped,
`Sale` counter zeroed, `Return` counter row dropped, inventory restored) — no script was kept.

## Consequences

- Contiguous, branch-scoped, human-readable numbers — one shared stream for sales + returns.
- Committed numbers are **never reused** — a later void or refund does not roll the counter back.
- Allocation adds a brief per-(tenant, branch) row lock to each checkout and each return; negligible
  for Phase 3 throughput, and it only serialises transaction documents within a single branch.
- A sale number and a return number can no longer be told apart by their string alone; a return is
  identified by its document row (`SaleReturn`) and its `OriginalSaleNumber` back-reference.
- The same table serves future purchasing / transfer numbering with no schema change.
