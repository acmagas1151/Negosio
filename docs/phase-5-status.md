# Phase 5 — Branch Management & Branch-Scoped Access — Status

**As of:** 2026-09-01
**Branch:** `feature/branch-management` (15 commits ahead of `master`, working tree clean, **not pushed, not merged**)
**Base:** `master` @ `61d2b01` (Phase 4 merged)

**Tests:** 88 unit / 161 integration — all green (baseline was 76 / 114).
Frontend: `oxlint` clean, `tsc -b && vite build` clean (bundle ≈ 474 kB js / 132 kB gz).

**Design + plan:** `docs/superpowers/specs/2026-09-01-branch-management-design.md`,
`docs/superpowers/plans/2026-09-01-branch-management.md`.

---

## What this phase does

Turns the always-there multi-branch architecture into a real feature, and locks every
non-Owner/Admin user to one branch across the whole system.

```
ROLE / CAPABILITY  → WHAT the user may do
BRANCH ASSIGNMENT  → WHERE they may do it
BRANCH ACTIVE      → WHETHER a branch-scoped user may use Negosio at all
```

- **All-branch roles:** Owner, Admin — never assigned a branch, tenant-wide access.
- **Branch-scoped roles:** Manager, Cashier, InventoryStaff, KitchenStaff, Viewer —
  `User.BranchId` is non-null (hard invariant), every branch-aware endpoint is forced to it.

---

## Done (Tasks 1–11 of 12)

### Data model — 4 migrations

| Migration | DB | What |
|---|---|---|
| `AddUserBranch` | tenant | `User.BranchId` (nullable FK) + `IX_Users_BranchId` + backfill: every existing non-Owner/Admin user bound to the tenant's sole branch |
| `AddStaffInvitationBranch` | platform | `StaffInvitation.BranchId` (carried value, validated at invite/accept) |
| `PerBranchDocumentNumberUniqueness` | tenant | `Sale`/`SaleReturn` receipt-number unique index moved from `(TenantId, Number)` to `(TenantId, BranchId, Number)` — each branch runs its own `0000001` sequence |
| `SessionOwnerOpenIndex` | tenant | filtered unique `IX_RegisterSessions_OpenedByUserId_Open` → at most one open session per user |

No data migration needed for the D8→D7 or per-branch-index changes (pre-production).

### Branch Management v1 (Owner / Admin only — `BranchManage` policy)

- `Branch` domain: `UpdateDetails` (name + address; **code is immutable**), `Deactivate`, `Reactivate`.
- `BranchManagementService` + `BranchValidators`.
- API: `GET /api/branches?includeInactive=`, `GET /{id}`, `POST`, `PUT /{id}`,
  `POST /{id}/deactivate`, `POST /{id}/reactivate`.
- **Last active branch cannot be deactivated** → `LAST_ACTIVE_BRANCH` (409).
- Deactivation never cascades — no user move, no session close, no data delete.
- Frontend: `/branches` page + `BranchFormModal` + `branch:manage` capability + nav item
  (`RequireCapability` guards the route).
- `GET /api/branches` returns **only the caller's branch** for branch-scoped users.

### Branch-scoped enforcement — `BranchAccessResolver`

One Application service that turns "requested branchId" into "allowed branchId or 403/404".
Wired into: dashboard metrics, inventory list/adjust/opening-stock, stock movements,
registers list/create, POS catalog + barcode, checkout, sales list, sale detail + receipt
(foreign → 404, not 403, to hide existence), returns. Owner/Admin keep full range; a
branch-scoped user gets `BRANCH_FORBIDDEN` (403) on a foreign target.
Dashboard "Branches" metric now counts **active** branches; scoped users see their branch's
metrics only.

### Authentication — branch-active gate

- **Login** (`AuthService`): branch-scoped user whose branch is inactive → `400 BRANCH_INACTIVE`
  ("Your assigned branch is currently inactive. Please contact your administrator.").
- **Per request** (`BranchAccessMiddleware`, after tenant routing): one indexed tenant-DB
  lookup on every authenticated branch-scoped request → `401 BRANCH_INACTIVE` the moment the
  branch is deactivated. No token blacklist, no Redis. Owner/Admin and anonymous requests
  untouched. Recovery = reactivate the branch **or** reassign the user.

### Register-session model

- Two DB-enforced invariants: one open session per register, one open session per user.
  `OpenAsync` translates the races → `REGISTER_SESSION_ALREADY_OPEN` / `CASHIER_SESSION_OPEN`.
- **Ownership**: checkout and normal close require `session.OpenedByUserId == currentUser`
  → else `SESSION_NOT_OWNED` (403). Applies to everyone (no operate-through override).
- `GET /api/register-sessions/current` returns the caller's own open session.
- **Force-close** — `POST /api/register-sessions/{id}/force-close`, **Owner/Admin only**
  (`RegisterForceClose` policy, *not* Manager). Same cash reconciliation;
  `ClosedByUserId` = the admin, `OpenedByUserId` untouched; works on an inactive branch.
  (Backend done; no dedicated UI button yet — admin API action.)

### POS operational flow

- New `GET /api/pos/context` → `{ branchId, branchName, canPickBranch, branches[] }`.
  Cashier: `canPickBranch: false`. Owner/Admin with 1 active branch: false. With ≥2: true.
- New `GET /api/pos/registers?branchId=` → active registers in the resolved branch, each
  with `openSession: { openedByName, mine } | null` (scoped user's foreign `branchId` → 403).
- `PosPage` rewritten: cashier → **no branch picker** → RegisterPicker
  (*Available* / *In use by \<name\>* disabled / *Your open session — Continue*) → opening-cash
  screen with **← Back to registers** and **Exit POS** → terminal. Owner/Admin multi-branch →
  BranchPicker (persisted, "Switch branch") → RegisterPicker → …
- Exit ≠ Close: Exit → dashboard, session stays open; re-entering shows "Continue".
  Close Session is the only path that reconciles + releases the register.

### Cashier register-access lockout (locked rule)

- `/registers` route + nav gate moved `pos:operate` → `register:manage` (Owner/Admin/Manager).
- "Reports — Soon" nav placeholder gated on `register:manage` — **absent**, not greyed, for a Cashier.
- Dashboard "Manage registers" quick action gated on `register:manage`.
- A Cashier selects/opens a register **only from the POS flow**.

### Transaction number format (locked rule)

- `DocumentNumberService`: `D8` → **`D7`** for Sale/Return (`0000001`, not `00000001`).
  Architecture unchanged — atomic per-(tenant, branch) counter, shared by sales + returns,
  never `COUNT(*)+1`, never reused. Property names `SaleNumber` / `ReturnNumber` unchanged,
  no schema column. ADR 0007 updated.
- UI: "Sale #0000001", "Return 0000002 · for sale 0000001".

### Staff ↔ branch (Phase 4 surface changes)

- `InviteStaffRequest` / `ChangeStaffRoleRequest` gain optional `BranchId`; branch **required**
  for a branch-scoped role, **rejected** for Owner/Admin, must be **active**.
- `ChangeRoleAsync` reconciles the branch: all-branch→scoped needs one; scoped→all-branch
  clears it; scoped→scoped keeps unless a new one is supplied.
- New `POST /api/staff/{id}/branch` (`ChangeBranchAsync`) — immediate effect, no re-login;
  **blocked with `STAFF_HAS_OPEN_REGISTER_SESSION`** while the user holds an open session.
- `StaffMemberDto` + list + page gain Branch column; invite modal + change-role modal get a
  Branch field; new Change-branch modal.
- Invitation acceptance writes the branch onto the tenant `User`.

### New error codes

`LAST_ACTIVE_BRANCH`, `BRANCH_INACTIVE`, `BRANCH_FORBIDDEN`, `SESSION_NOT_OWNED`,
`CASHIER_SESSION_OPEN`, `STAFF_HAS_OPEN_REGISTER_SESSION`
(reuse existing `BRANCH_NOT_FOUND`, `DUPLICATE_BRANCH_CODE`, `REGISTER_SESSION_ALREADY_OPEN`).

---

## Not done

### Task 12 — verification + report (remaining work in this phase)

- The scripted 23-step headless-Chrome walkthrough (spec §12): cashier lock, register
  picker states, opening-cash Back/Exit, deactivate→lockout→force-close→reactivate→reassign,
  per-branch numbering, foreign-branch rejection, RBAC regression sweep, zero console errors.
- The 9-section final report (Architecture / Backend / Frontend / Multi-Branch Correctness /
  Security / Tests / Browser Verification / Git State / Remaining Limitations).
- Full `dotnet test Negosio.sln` final count confirmation.

### Deliberately out of scope (carry forward)

no staff-to-branch *assignment beyond one branch* (no multi-branch membership) · no stock
transfers · no branch-specific tax / pricing / catalog · no consolidated/branch reporting ·
no branch-level dashboards beyond the scoped metrics · no operate-another's-session override
(force-close only) · no Void · no Reporting phase · no offline selling · no F&B · no tenant
timezone · PHP currency fixed · one email = one tenant · no separate cashier PIN.

### Known gaps / follow-ups

- **Force-close has no UI** — it's an API-only admin action right now. A small "force-close"
  button on a stuck-session view (Registers page, Owner/Admin) would close the loop.
- A branch-scoped user whose branch is deactivated **keeps** their assignment (by design) —
  so after reactivation they're back automatically. No UI surfaces "these N staff are
  currently locked out" to the Owner; the deactivate confirm dialog only warns generally.
- `POST /api/pos/registers` availability is a snapshot — the picker refetches on
  back/`REGISTER_SESSION_ALREADY_OPEN`, but there's no live push.

---

## Suggested next steps (for discussion)

1. **Finish Phase 5** — run Task 12 (browser walkthrough + final report), then review +
   merge `feature/branch-management` to `master`.
2. **Small close-the-loop items** (could fold into Phase 5 review or a quick follow-up):
   force-close UI button; "locked-out staff" hint on branch deactivate.
3. **Phase 6 candidates** (pick one): Void/refund-without-return · Reporting v1 (sales by
   day / by branch / by cashier, X/Z reports) · Purchasing & supplier receiving (the
   `DocumentNumberCounter` already reserves `PurchaseOrder`) · Stock transfers between
   branches · F&B vertical (tables, KDS, KitchenStaff becomes real).

---

## Working dev logins (seeded this session, `Negosio.InvUITest_MAIN_204CD2A1`)

| Role | Email | Password | Branch |
|---|---|---|---|
| Owner | `invui@example.com` | `SecurePassword123!` | all |
| BGC cashier | `bea@negosio.test` | `Cashier123!` | BGC Branch |
| BGC cashier | `bgc.cashier@negosio.test` | `Cashier123!` | BGC Branch |
| Main cashier | `cashier@negosio.test` | `Cashier123!` | Main Branch |

Branches: **Main Branch** (MAIN) + **BGC Branch** (BGC), both active + stocked.
Registers: Main POS 1, BGC POS 1, BGC POS 2.
