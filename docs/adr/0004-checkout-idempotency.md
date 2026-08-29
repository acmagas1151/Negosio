# ADR 0004 — Checkout idempotency via ClientRequestId

- Status: Accepted
- Date: 2026-08-29
- Phase: 3 (Retail POS)

## Context

A cashier taps **Pay**, the server completes the sale, and the network response is lost. The
cashier taps **Pay** again. Without protection this creates two sales, two payments and two
inventory deductions. This is a standard production POS hazard, not an edge case.

## Decision

The client generates one `ClientRequestId` (GUID) when a checkout attempt begins and **reuses it**
for every retry of that same attempt. `Sale.ClientRequestId` is persisted with a unique index
`(TenantId, ClientRequestId)`.

`CheckoutService.CheckoutAsync`:

1. **Pre-check** (before the transaction): `SELECT Sale WHERE TenantId=@t AND ClientRequestId=@c`.
   If found, return that sale's result with HTTP **200** and do nothing else.
2. Otherwise open the transaction, allocate the sale number, and `INSERT` the `Sale` row first
   (`SaveChanges`). This is the **race gate**: two concurrent retries that both passed the
   pre-check collide here; the loser catches the unique violation, rolls back (only the `Sale`
   insert was attempted — no inventory touched yet), re-reads the winner and returns it (200).
3. Only after the `Sale` row is safely inserted does checkout deduct inventory and write stock
   movements.

`DUPLICATE_CHECKOUT_REQUEST` exists as an error code, but the happy path **returns the existing
successful result, not an error** — `SaleResultDto.WasExistingRequest` is `true` on a replay.

## Consequences

- Exactly one sale / payment set / inventory deduction per `ClientRequestId`, even under retries
  or concurrent double-submits.
- The client must not regenerate the id on each button press (the React checkout modal generates it
  once when the modal opens and clears it only on a confirmed success).
- The pre-check is a fast index seek; the gate only matters for true concurrent duplicates.
