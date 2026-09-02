# Phase 6 — Void Sales & Cash Operations — Design

**Status:** approved design (user-locked rules, 2026-09-02)
**Branch:** `feature/void-cash-operations` (off `master`)
**Do NOT auto-merge.** Stop after implementation + full verification + final report.

---

## 1. Purpose & scope

Complete the day-to-day retail transaction-correction and register cash-management
lifecycle before Reporting. Two things ship together:

1. **Sale voiding** — full-sale-only reversal of a `Completed` Sale, with a two-track
   authorization model (direct vs manager-approved) and full audit trail, inventory
   reversal, and cash-effect exclusion from reconciliation.
2. **Register cash movements** — `CashIn`/`CashOut` entries against an open session, folded
   into the existing close-time reconciliation.

### Explicitly NOT in this phase

Line-level/partial void · editing a completed sale in place · Reporting (X/Z, sales-by-day)
· stock transfers · purchasing/receiving · F&B (tables/KDS) · offline selling ·
branch-specific tax/pricing/catalog · multi-currency · tenant timezone · a permission-grant
history/audit trail (row existence is the only state) · closed-session void of any kind ·
manager "operate another user's session" override (still force-close only, from Phase 5).

### Preserved limitations (carry into the final report)

no partial void · no line-level post-sale void · no multiple branch assignments · no stock
transfers · no branch-specific tax/pricing/catalog · no consolidated reporting · no
Reporting phase · no offline selling · no F&B · no tenant timezone · PHP currency fixed ·
one email = one tenant · no separate cashier PIN · no production invitation email provider
if still absent.

---

## 2. Void vs Return (locked)

| | Void | Return |
|---|---|---|
| When | The transaction itself was wrong (wrong payment, duplicate, mis-rung) | The sale was legitimate; goods come back later |
| Scope | Full sale only | Partial or full, per line |
| Number | Keeps original `SaleNumber`, no new document number | Gets its own `ReturnNumber` |
| Sale status | `Completed → Voided` | `Completed → Refunded/PartiallyRefunded` |
| Timing | Same shift, same UTC day, session still open | Any time after completion |
| Mutual exclusion | A Sale with any Return cannot be voided; a Voided Sale cannot be returned | — |

Void is never implemented as "create a Return" — it is a first-class status transition on
`Sale` with its own domain method, audit fields, and inventory/cash effects.

---

## 3. Void eligibility (locked, checked in this exact order)

```
1. Sale.Status == Completed                    → else SALE_NOT_VOIDABLE
2. Sale has no Returns                          → else SALE_HAS_RETURNS
3. Sale.RegisterSessionId refers to an OPEN session → else VOID_SESSION_CLOSED
4. Sale.CompletedAtUtc.Date == nowUtc.Date       → else VOID_CUTOFF_EXPIRED
```

Order matters for user-facing clarity (spec requirement: "the most useful reason"), and is
enforced identically regardless of actor — **authorization never overrides domain
eligibility**, including for Owner. `nowUtc` comes from the already-DI-registered
`TimeProvider` (`TimeProvider.System` in production; a `FakeTimeProvider`/manual advance in
tests) — no new clock abstraction.

A Sale whose session has already closed, or that is from a prior UTC day, is permanently
ineligible — the only path forward is a Return. This is what keeps `RegisterSession.Close()`
one-way and its `ExpectedCash`/`ClosingCash`/`CashDifference` fields forever frozen once
written; Void never needs to revisit a closed session.

---

## 4. Void authorization (locked)

| Actor | Behavior |
|---|---|
| Owner | Direct void, all branches |
| Admin | Direct void, all branches |
| Manager | Direct void, assigned branch only |
| Cashier **with** `SalesVoid` grant | Direct void, assigned branch only, reason required |
| Cashier **without** `SalesVoid` grant | Manager/Admin/Owner approval required (reason + approver credentials) |
| InventoryStaff / Viewer / KitchenStaff | No void action at all (button hidden) |

The Void button is visible to Owner/Admin/Manager/Cashier regardless of the Cashier's grant
— the grant only decides which sub-flow (direct-reason vs approval-form) they see.

`VoidSaleService` itself must explicitly check the actor's role against exactly this four-role
set and reject anything else with a generic forbidden error — it must not rely solely on the
controller's `SalesView` policy (today Owner/Admin/Manager/Cashier, but a future policy change
elsewhere should not silently widen who this service treats as an authorized void actor), and it
must not treat "not Owner/Admin/Manager" as an implicit synonym for "Cashier."

### Audit identity (locked)

```
Cashier direct void:            VoidedByUserId = cashier,        ApprovedByUserId = null
Cashier with manager approval:  VoidedByUserId = cashier,        ApprovedByUserId = approver
Manager/Owner/Admin direct:     VoidedByUserId = acting user,     ApprovedByUserId = null
```

`VoidedByUserId` is always who clicked Void. `ApprovedByUserId` is populated only when the
approval sub-flow ran. Neither overwrites `Sale.CreatedByUserId` (the original cashier who
rang the sale, which may differ from `VoidedByUserId`).

---

## 5. Permission grant model

New concept, not an extension of anything existing — the current authorization model is
purely role→policy (`AuthorizationPolicies.cs`, `useCan.ts`), with no per-user override
table anywhere in the schema today.

```
UserPermissionGrant (tenant DB)
  Id              Guid
  TenantId        Guid
  UserId          Guid
  Permission      UserPermission enum   (SalesVoid = 1; only value for now)
  GrantedByUserId Guid
  GrantedAtUtc    DateTime
```

Unique constraint `(TenantId, UserId, Permission)` — no duplicate grants.

**Row existence = active permission.** Revoke deletes the row. No soft-revoke, no
grant/revoke history — the Void's own audit fields (`VoidedByUserId`/`ApprovedByUserId`)
already capture who actually acted; a separate permission-history trail is out of scope.
Read fresh from the tenant DB on every void attempt — never cached in the JWT — so
grant/revoke takes effect on the cashier's very next request, matching the "resolved fresh
per request" pattern Phase 5 established for branch access (`BranchAccessResolver`,
`BranchAccessMiddleware`).

### Grant/revoke authorization (locked)

```
Owner/Admin  → grant/revoke SalesVoid for any Cashier in the tenant
Manager      → grant/revoke SalesVoid only for Cashiers in the Manager's own branch
Cashier      → cannot modify their own permission
other roles  → cannot manage this permission at all
```

Enforced via `BranchAccessResolver` (Manager path) plus a direct role check, in a new
`SalesVoidPermissionService`.

### Staff visibility gap (resolved)

Today `StaffController` is entirely `StaffManage`-gated (Owner/Admin only) — a Manager has
no route to reach staff data at all, so "Manager can grant/revoke for their own-branch
Cashiers" has no UI to hang off without a visibility change. Resolution, kept minimal:

- `GET /api/staff` and `GET /api/staff/{id}` get a new `StaffView` policy override
  (Owner/Admin/Manager) in place of the controller-level `StaffManage`. For a non-all-branch
  caller (Manager), `StaffService.ListAsync` filters strictly to `m.BranchId == assignedBranchId`
  — **not** `m.BranchId == assignedBranchId || m.BranchId == null`. A null `BranchId` always
  means Owner/Admin (Section 2 of the Phase 5 design), and a Manager must never see Owner/Admin
  rows, so the null-branch case is excluded outright rather than treated as "visible to
  everyone." Owner/Admin keep the unfiltered, tenant-wide roster.
- All other staff endpoints (`invitations`, `role`, `branch`, `deactivate`, `reactivate`)
  keep the existing `StaffManage` (Owner/Admin-only) policy, unchanged from Phase 4/5.
- Only the new `PUT /api/staff/{id}/permissions` (Section 10) is Manager-reachable among
  writes, via its own `StaffPermissionManage` override, with the branch + Cashier-only
  check enforced in-service.

---

## 6. Approver reauthentication

New `ApproverVerificationService`. Reuses the exact password-verification path
`AuthService.LoginAsync` already uses (`IPasswordHasher` + `IPlatformDbContext`,
timing-equalized hash comparison for unknown emails) but stops short of issuing a JWT.

```
1. Normalize approver email, look up PlatformUserLogin by EmailNormalized
   → not found: use timing-equalizer hash, then fail generically (INVALID_APPROVER_CREDENTIALS)
2. Verify password via IPasswordHasher.Verify(...)
   → mismatch: INVALID_APPROVER_CREDENTIALS (same generic message as step 1)
3. PlatformUserLogin.TenantId must equal the current (Cashier's/Sale's) tenant
   → mismatch: INVALID_APPROVER_CREDENTIALS (do not leak that the email exists in another tenant)
4. Load tenant User by Id (PlatformUserLogin.Id == tenant User.Id); must be IsActive
   → inactive: INVALID_APPROVER_CREDENTIALS
5. Role must be Manager, Admin, or Owner
   → else: VOID_APPROVER_NOT_AUTHORIZED
6. If role == Manager: User.BranchId must equal Sale.BranchId, and that branch must be active
   → else: VOID_APPROVER_WRONG_BRANCH
   (Admin/Owner skip this check — tenant-wide)
```

The approver's password is never logged, never persisted, and never appears in exception
messages. The Cashier's own session/JWT is untouched — this is a one-shot reverification,
not an impersonation or session upgrade. Approval is single-use: it authenticates exactly
the one void request it was submitted with; nothing is cached or reusable for a subsequent
void.

---

## 6.5. Concurrency safety (locked)

Void's eligibility checks (status, returns, session-open, cutoff) must be re-verified inside the
same database transaction as inventory reversal and `Sale.Void(...)` + `SaveChanges` — not just
once before the transaction opens. Two mechanisms, both required:

- **`Sale.RowVersion`** — a new SQL Server `rowversion` concurrency token column. Protects any two
  competing writes to the *same Sale row* (double-void, or void racing a concurrent Return):
  whichever transaction's `SaveChangesAsync` commits first wins; the loser gets
  `DbUpdateConcurrencyException`, which both `VoidSaleService` and `ReturnService` catch by
  re-fetching the sale's now-current state and throwing the *accurate* error for that state (e.g.
  `SALE_NOT_VOIDABLE` if it's now Voided, `SALE_HAS_RETURNS` if a return landed first) rather than
  a generic conflict. Transactional rollback on that exception also undoes any inventory movement
  already written earlier in the same (losing) transaction — inventory is never left double-applied.
- **A pessimistic lock on the `RegisterSession` row** (`SELECT ... WITH (UPDLOCK, HOLDLOCK)`),
  taken as the very first statement inside both `VoidSaleService.VoidAsync`'s transaction (before
  reading session-open status) and `RegisterSessionService.ReconcileAndCloseAsync` (before its
  reconciliation sums). `RowVersion` alone cannot protect void-vs-close: Void never *writes* to
  `RegisterSessions`, so there is no natural optimistic-concurrency collision between the two to
  detect. The pessimistic lock instead fully serializes the two operations against the same
  session — whichever acquires the lock first runs its entire read-then-write sequence to
  completion (commit or rollback) before the other can even begin reading, so a session can never
  close with a reconciliation that's stale relative to a void that just committed, or vice versa.

## 7. Inventory reversal

New `StockMovementType.SaleVoid = 10` (appended; existing 1-9 never renumbered). New
`IInventoryPosting.ReverseForVoidAsync(tenantId, branchId, productVariantId, quantity,
saleId, userId, cancellationToken)`, modeled on the existing never-fails
`RestockForReturnAsync` (creates `BranchInventory` row if somehow missing, writes one
`StockMovement` per line with `referenceType: "Sale"`, `referenceId: saleId`). Called once
per `SaleItem` inside the same DB transaction as `Sale.Void(...)` and `SaveChanges` — a Sale
is never left `Voided` with inventory unrestored, and vice versa. Original `Sale`/`Payment`/
`StockMovement` rows from the original checkout are never deleted or mutated — the void is
a new, explicit reversal movement, preserving the full ledger trace
(`Sale -3` / `SaleVoid +3`).

Idempotency: eligibility check #1 (`Status == Completed`) already prevents voiding twice —
a second attempt fails fast with `SALE_NOT_VOIDABLE` before any inventory or session logic
runs, so double-reversal is structurally impossible, not just guarded by a flag.

---

## 8. Cash accounting

Because Void is now only reachable while the session is still open (Section 3), there is
**no closed-session cash correction to design** — `RegisterSession.Close()`'s one-way
guard and the fact that `ExpectedCash`/`ClosingCash`/`CashDifference` are computed once,
live, at close time (confirmed in `RegisterSessionService.ReconcileAndCloseAsync`) are
untouched by this phase. No new ledger table for void's cash effect — it's fully derivable
from `Sale.Status == Voided` + the original `Payment` rows at reconciliation time.

### Reconciliation formula (extends the existing live query)

```
Expected Cash =
    Opening Cash
  + Gross cash sales   (all Completed-or-Voided sales' cash Payments in this session)
  - Voided cash sales   (cash Payments belonging to sales now Status == Voided)
  - Refund cash out     (existing RefundPayments term, unchanged)
  + Cash In
  - Cash Out
```

`ReconcileAndCloseAsync` gets two new query terms (`voidedCashOut`, `cashIn`/`cashOut` sums
from `RegisterCashMovement`) alongside its existing `cashIn`/`cashOut` variables (renamed
for clarity: `grossCashSales`, `voidedCashSales`, `refundCashOut`, `cashMovementIn`,
`cashMovementOut`). The breakdown is exposed via a new response DTO so the close-session UI
can render the exact math unambiguously:

```
Opening cash          ₱5,000
Gross cash sales       ₱5,000
Voided cash sales        -₱500
Net cash sales         ₱4,500
Refund cash out          -₱200
Cash in                  ₱500
Cash out               -₱1,000
------------------------------
Expected cash          ₱8,800

Counted cash            ₱8,750
Difference                -₱50
```

("Net cash sales" is a display-only derived line — `Gross − Voided`. The underlying stored/
summed fields are gross and voided separately, never a pre-subtracted number, so nothing is
subtracted twice.)

### `RegisterCashMovement` (new entity)

```
RegisterCashMovement (tenant DB)
  Id                Guid
  TenantId          Guid
  BranchId          Guid
  RegisterSessionId Guid
  Type              CashMovementType   (CashIn = 1, CashOut = 2)
  Amount            decimal            (> 0, enforced by domain + validator)
  Reason            string             (required)
  CreatedByUserId   Guid
  CreatedAtUtc      DateTime
```

Append-only, like `StockMovement` — no edit/delete. Amount sign is never negative; direction
comes from `Type`, never from a signed amount (explicit spec requirement).

### Cash movement authorization (locked)

```
Cashier  → create CashIn/CashOut only on their own open session (session.OpenedByUserId == currentUser)
Manager  → view branch session movements only (no create-on-behalf-of in v1)
Owner/Admin → view all
```

Validation order for create: authenticated → branch access → session exists → session open
→ `session.OpenedByUserId == currentUser` → `amount > 0` → `reason` present.

---

## 9. Sale status & data model changes

`SaleStatus.Voided` (already defined, previously unused — `Completed=1, Voided=2,
Refunded=3, PartiallyRefunded=4`) becomes active. New
`Sale.Void(Guid voidedByUserId, string reason, Guid? approvedByUserId, DateTime nowUtc)` domain
method, guarded the same way `RegisterSession.Close()` guards against re-entry (throws if
`Status != Completed`; the service layer runs the same check first and throws the typed
`AppException` — the domain guard is defense in depth, not the primary error path, matching how
`RegisterSessionService` layers its own checks in front of `RegisterSession.Close()`'s guard).
**`nowUtc` is a required parameter, not `DateTime.UtcNow` called inside the method** — the caller
(`VoidSaleService`) captures one `TimeProvider`-sourced timestamp per void attempt and reuses it
for both the same-UTC-day eligibility comparison and `VoidedAtUtc`, so the two can never disagree
even under a slow request that straddles midnight. Reuses the existing `VoidedAtUtc`/`VoidReason`
columns (present since the tenant baseline migration, previously unset — confirmed via
`SaleConfiguration.cs`, which already has a `VoidReason` mapping). **Three** new columns, since
`Sale` currently has no actor field for the void at all and no concurrency token:
`Sale.VoidedByUserId` (nullable `Guid`, set whenever `Status == Voided`),
`Sale.ApprovedByUserId` (nullable `Guid`, set only when the approval sub-flow ran), and
`Sale.RowVersion` (SQL Server `rowversion`, EF `IsRowVersion()` — see Section 6.5).

Allowed transition: `Completed → Voided` only. `PartiallyRefunded → Voided`,
`Refunded → Voided`, `Voided → *` all remain impossible (guarded by eligibility check #1
plus the domain method's own guard as defense in depth).

---

## 10. API surface

```
POST /api/sales/{id}/void
  policy: SalesView (controller-level, unchanged — already Owner/Admin/Manager/Cashier
          only, so InventoryStaff/Viewer/KitchenStaff are rejected by this claim check
          before the action method ever runs). Direct-vs-approval branching for Cashier is
          then resolved in-service (grant lookup), since that distinction depends on a
          per-user grant, not a static role claim.
  body: { reason: string, approval?: { approverEmail: string, approverPassword: string } }
  → 200 SaleDetailDto (includes Voided/VoidedBy/ApprovedBy/VoidReason/VoidedAtUtc)

POST /api/register-sessions/{id}/cash-movements
  policy: PosOperate (matches existing register-sessions controller base policy)
  body: { type: 'CashIn' | 'CashOut', amount: decimal, reason: string }
  → 201 RegisterCashMovementDto

GET /api/register-sessions/{id}/cash-movements
  policy: PosOperate (Cashier: own session only, enforced in-service; Manager: branch; Owner/Admin: any)

PUT /api/staff/{id}/permissions
  policy: new StaffPermissionManage policy (Owner/Admin/Manager) overriding the
          StaffController's controller-level StaffManage (Owner/Admin-only) — same
          override pattern as CreateReturn (RefundManage over SalesView) and force-close
          (RegisterForceClose over PosOperate). Branch restriction for Manager (own branch
          only, target must be a Cashier) and self-grant denial enforced in-service.
  body: { salesVoid: boolean }
  → 200 StaffMemberDto (includes salesVoid flag)
```

`VoidSaleRequest.approval` is omitted entirely (not just null) when the actor has direct
authority — the API never asks for credentials it won't use. Response DTOs never echo
`approverPassword` under any circumstance, and it is excluded from request logging.

### New error codes (Phase 6 block in `ErrorCodes.cs`)

```
SALE_NOT_VOIDABLE
SALE_HAS_RETURNS
VOID_SESSION_CLOSED
VOID_CUTOFF_EXPIRED
VOID_APPROVAL_REQUIRED
INVALID_APPROVER_CREDENTIALS
VOID_APPROVER_NOT_AUTHORIZED
VOID_APPROVER_WRONG_BRANCH
CASH_MOVEMENT_INVALID_AMOUNT
CASH_MOVEMENT_SESSION_CLOSED
CASH_MOVEMENT_NOT_OWNER
```

---

## 11. Frontend

- **Sale Detail**: `Void sale` button shown for Owner/Admin/Manager/Cashier when the sale
  DTO reports it eligible (mirrors backend order — status/returns/session/cutoff — with a
  friendly reason when ineligible, e.g. "Use the return/refund process instead"). Two modal
  variants sharing one component, switched on whether the current Cashier has the grant:
  reason-only vs approval-form (Manager account, Password, Reason). Invalid approval shows
  a generic "Invalid manager credentials." / "This manager cannot approve voids for this
  branch." without leaking account existence. On success: `VOIDED` badge, `Voided by`,
  `Approved by` (only if set), `Void reason`, `Voided at`; `Void sale`/`Start Return` both
  disappear; receipt view reflects `VOIDED` too.
- **Staff Management**: on Cashier rows only, a focused toggle/modal — "Allow voiding
  completed sales" — imitating the existing `ChangeRoleModal` pattern (`useMutation` +
  `qc.invalidateQueries(['staff'])`, `Modal`/`Button`/`Callout`/`useToast` from `../ui`).
  Owner/Admin can edit any Cashier; Manager only same-branch Cashiers (button hidden/disabled
  otherwise, backend authoritative regardless). New capability `'staff:permissions'`
  (Owner/Admin/Manager) alongside the existing `'staff:manage'` (Owner/Admin only,
  unchanged): the `/staff` nav entry and route become reachable on **either** capability;
  a Manager's `StaffPage` renders the same table (server-filtered to their branch per
  Section 5) but with Invite/Change role/Change branch/Deactivate/Reactivate hidden —
  only the permission toggle on Cashier rows is active for them.
- **POS session controls**: `Cash In` / `Cash Out` buttons alongside `Close session`, each a
  small `Modal` (Amount, Reason) posting to the new endpoint and invalidating the current
  session query.
- **Close Session modal**: extended to render the full breakdown (Section 8) when the new
  DTO fields are present, not just the final Expected/Counted/Difference numbers.
- **Sales list**: `Voided` status badge/filter alongside existing statuses; voided sales
  remain searchable by number and stay in history.
- **`useCan.ts`**: two new capabilities, `'sales:void'` (Owner/Admin/Manager/Cashier — the
  Cashier grant nuance is resolved server-side/per-sale-DTO, not by this coarse capability)
  and `'register:cash-movement'` (Owner/Admin/Manager/Cashier, mirroring `pos:operate`).

---

## 12. Tests (see original brief for the full enumerated list — summarized here)

Integration: direct void (Owner/Admin/Manager, own vs other branch), Cashier with/without
grant (including the full approval round-trip and wrong-password/wrong-branch-manager
cases), already-voided, sale-with-returns, return-after-void, inventory reversal (correct
branch only), session-closed rejection, cutoff-expired rejection (using `TimeProvider`
advance, not wall-clock sleep), permission grant/revoke (including branch-restricted
Manager grant and self-grant denial) with immediate effect and no re-login, Cash In/Out
(ownership, closed-session rejection), and exact-decimal reconciliation math across
opening/gross-sales/voided-sales/refunds/cash-in/cash-out.

Unit: `Sale.Void()` transition guards, permission enum/grant rules, cash movement amount
validation, reconciliation arithmetic.

No fake-repository DB tests — integration tests use the real `WebApplicationFactory` +
SQL Server pattern already established in this repo.

---

## 13. Browser verification plan

Real API + frontend, at least four accounts: Owner, BGC Manager, BGC Cashier-with-grant,
BGC Cashier-without-grant. Twelve scenarios per the original brief: approval flow (valid +
invalid), direct-permission flow, permission revoke (immediate, no re-login), Manager grant
management (own branch only), voided-sale UI, inventory reversal, Cash In, Cash Out, Close
Session reconciliation breakdown, full RBAC matrix, branch boundary (BGC vs MAIN), and a
regression smoke pass over all Phase 5 routes confirming Cashier still cannot see
Registers/Reports/Branches/Staff/Settings, and that a BGC Manager's `/staff` view shows
only BGC staff with only the permission toggle active (no Invite/role/branch/deactivate).
