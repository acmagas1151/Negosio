# Phase 5 — Branch Management & Branch-Scoped Access — FINAL REPORT

**Date:** 2026-09-01
**Branch:** `feature/branch-management` — HEAD after this doc, **19 commits ahead of `master`**,
working tree clean, **not pushed, not merged**. Base `master` @ `61d2b01`.
**Status:** functionally complete + verified. Stopped for review.

**Automated verification (actual):**

| Command | Result |
|---|---|
| `dotnet build Negosio.sln` | Build succeeded — 0 warnings, 0 errors |
| `dotnet test Negosio.sln` | **88 unit / 162 integration — 0 failed, 0 skipped** (baseline 76 / 114) |
| `npm run lint` (oxlint) | clean |
| `npm run build` (`tsc -b && vite build`) | clean — 474.67 kB js / 132.29 kB gz |

Design: `docs/superpowers/specs/2026-09-01-branch-management-design.md`
Plan: `docs/superpowers/plans/2026-09-01-branch-management.md`

---

## 1. Architecture

**Branch model.** `Branch` (tenant DB) is unchanged structurally: `Id, TenantId, Name, Code,
AddressLine1/2, City, Province, PostalCode, IsActive, CreatedAtUtc, UpdatedAtUtc`, unique
`IX_Branches_TenantId_Code`. New domain methods only: `UpdateDetails` (name + address),
`Deactivate`, `Reactivate`. **Branch code is immutable after creation** (it seeds the frozen
tenant-DB-name suffix and reserved PO/transfer number prefixes; editing it is disallowed).

**Branch lifecycle.** `Active` ⇄ `Inactive`, never deleted. Deactivation flips `IsActive` and
nothing else — no user move, no session close, no data delete. The tenant's **last active
branch cannot be deactivated** (`LAST_ACTIVE_BRANCH`, 409). Recovery from a deactivated
branch = reactivate it **or** reassign its staff.

**Roles.**

```
All-branch:    Owner, Admin           User.BranchId == null   tenant-wide access
Branch-scoped: Manager, Cashier,       User.BranchId != null   every branch-aware op forced
               InventoryStaff,                                 to that branch; foreign → 403/404
               KitchenStaff, Viewer
```

```
allow = capability/role permits the action
      AND (Owner/Admin  OR  requested resource ∈ user's assigned branch)
      AND (Owner/Admin  OR  user's assigned branch is Active)   ← authentication gate
```

**Branch-active authentication.** A branch-scoped user may sign in and keep using Negosio
**only while their assigned branch is active**:

- *Login* (`AuthService.LoginAsync`): inactive branch → `400 BRANCH_INACTIVE` with a
  user-facing message.
- *Every authenticated request* (`BranchAccessMiddleware`, immediately after
  `TenantResolutionMiddleware`): one indexed tenant-DB lookup → `401 BRANCH_INACTIVE` the
  moment the branch is deactivated. No token blacklist, no Redis. Owner/Admin and anonymous
  requests pass straight through and never touch the tenant DB.

**`BranchAccessResolver`** (scoped Application service) is the single choke point. It turns a
request's "which branch?" into an allowed branch id or an exception:

- `AssignedBranchIdAsync()` → `null` for Owner/Admin, the assigned id for scoped users.
- `ResolveListFilterAsync(requested)` → list/read filter (Owner/Admin: `requested`; scoped:
  always their branch).
- `ResolveTargetBranchAsync(requested, allowInactive)` → the branch to write to; scoped user
  supplying a different id → `ForbiddenAppException(BRANCH_FORBIDDEN)`; then asserts the
  branch is active unless `allowInactive`.

**Staff `BranchId` model.** `User.BranchId` (tenant DB, nullable FK). `StaffInvitation.BranchId`
(platform DB — a carried value, validated against the tenant DB at invite and accept time; no
FK because branches live in a different database). Branch is **required** for a branch-scoped
role, **rejected** for Owner/Admin, and must be **active** at assignment time. Acceptance writes
the branch onto the new `User`. Reassignment (`ChangeBranchAsync`) takes effect on the user's
next request (branch is not in the JWT — no re-login), but is **blocked while the user holds an
open register session** (`STAFF_HAS_OPEN_REGISTER_SESSION`). Role changes reconcile the branch:
all-branch→scoped needs one; scoped→all-branch clears it; scoped→scoped keeps unless a new one
is supplied.

**POS register / session ownership model.**

- `GET /api/pos/context` resolves the branch (auto for scoped roles and single-branch owners;
  a picker for Owner/Admin with ≥2 active branches).
- `GET /api/pos/registers` lists the resolved branch's active registers, each with
  `openSession: { openedByName, mine } | null`.
- Two DB-enforced invariants: **one open session per register** (existing filtered unique
  index) and **one open session per user** (new `IX_RegisterSessions_OpenedByUserId_Open`).
- **Ownership**: checkout and normal close require `session.OpenedByUserId == currentUser`
  → `SESSION_NOT_OWNED` (403) otherwise. Applies to everyone — no operate-through override.
- **Force-close** (Owner/Admin only, `RegisterForceClose` policy, *not* Manager): same cash
  reconciliation; `ClosedByUserId` = the acting admin, `OpenedByUserId` untouched; works on
  an inactive branch (deliberate cleanup path).
- `GET /api/register-sessions/current` returns the **caller's own** open session.
- Exit ≠ Close: leaving the POS UI keeps the session open and the register locked to the
  cashier; only Close/Force-close reconciles cash and releases the register.

**Transaction numbering.** `DocumentNumberService` formats `Sale`/`Return` as a bare
**zero-padded 7-digit** running number (`0000001`). One atomic per-`(TenantId, BranchId)`
counter row, shared by sales and returns (both allocate with `DocumentNumberType.Sale`),
`UPDATE … OUTPUT` — never `COUNT(*)+1`, never reused, no prefix, no branch code. `Sale`/
`SaleReturn` unique indexes are `(TenantId, BranchId, <Number>)` so **each branch runs its own
`0000001` sequence**. Property names `SaleNumber` / `ReturnNumber` unchanged; no schema column.
ADR 0007 updated.

---

## 2. Backend

**Migrations (4):**

| Migration | DB | Contents |
|---|---|---|
| `AddUserBranch` | tenant | `User.BranchId` nullable FK + `IX_Users_BranchId` + backfill `UPDATE Users SET BranchId = (SELECT TOP 1 Id FROM Branches) WHERE [Role] NOT IN (1,2)` |
| `AddStaffInvitationBranch` | platform | `StaffInvitation.BranchId` nullable |
| `PerBranchDocumentNumberUniqueness` | tenant | drop `IX_Sales_TenantId_SaleNumber` / `IX_SaleReturns_TenantId_ReturnNumber`; add `(TenantId, BranchId, …)` uniques |
| `SessionOwnerOpenIndex` | tenant | filtered unique `IX_RegisterSessions_OpenedByUserId_Open` on `(TenantId, OpenedByUserId) WHERE [Status]=1` |

**Services (new / changed):**

- `BranchManagementService` (`IBranchManagementService`) — Get / Create / Update / Deactivate
  (last-active guard) / Reactivate. `BranchQueryService` widened (full DTO + `AssignedStaffCount`
  + scoped-user-sees-only-their-branch).
- `BranchAccessResolver` (`IBranchAccessResolver`) — new.
- `BranchValidators` — `CreateBranchRequestValidator`, `UpdateBranchRequestValidator`.
- `PosContextService` (`IPosContextService`) — `GetContextAsync`, `GetRegistersAsync`.
- Wired `IBranchAccessResolver` into: `DashboardService`, `InventoryService`
  (list/adjust/movements), `RegisterService` (list/create), `PosCatalogService`,
  `CheckoutService`, `SaleQueryService` (list + 404-on-foreign detail), `ReceiptService`,
  `ReturnService` (404-on-foreign sale; **no** active-branch guard so Owner/Admin can refund
  against inactive branches).
- `RegisterService.ListAsync/GetAsync` now stitch each register's current open session
  (`RegisterDto.OpenSession`) for the management view.
- `RegisterSessionService` — `CASHIER_SESSION_OPEN` translation, ownership checks on
  checkout/close, `ForceCloseAsync`, `GetCurrentAsync` filters by owner.
- `AuthService.LoginAsync` — branch-active check for scoped roles.
- `StaffService` — `ResolveInvitationBranchAsync`, role↔branch reconciliation in
  `ChangeRoleAsync`, new `ChangeBranchAsync`; `StaffInvitationService.CreateTenantUserAsync`
  threads the branch.
- `DocumentNumberService` — `D8` → `D7`.

**Endpoints:**

| Method | Route | Policy |
|---|---|---|
| GET | `/api/branches?includeInactive=` | `[Authorize]` (scoped → only their branch) |
| GET | `/api/branches/{id}` | `BranchManage` |
| POST | `/api/branches` | `BranchManage` |
| PUT | `/api/branches/{id}` | `BranchManage` |
| POST | `/api/branches/{id}/deactivate` \| `/reactivate` | `BranchManage` |
| GET | `/api/pos/context` | `PosOperate` |
| GET | `/api/pos/registers?branchId=` | `PosOperate` |
| POST | `/api/register-sessions/{id}/force-close` | `RegisterForceClose` (Owner/Admin) |
| POST | `/api/staff/{id}/branch` | `StaffManage` |
| — | `InviteStaffRequest` / `ChangeStaffRoleRequest` gain optional `BranchId` | |

**Policies:** new `BranchManage` (Owner/Admin), `RegisterForceClose` (Owner/Admin). Existing
`RegisterManage` (Owner/Admin/Manager) now also gates the `/registers` route on the frontend.

**Middleware:** `BranchAccessMiddleware` after `TenantResolutionMiddleware`.

**Domain errors (new):** `LAST_ACTIVE_BRANCH` (409), `BRANCH_INACTIVE` (400 at login / 401
per-request / 400 at staff-branch-assign), `BRANCH_FORBIDDEN` (403), `SESSION_NOT_OWNED`
(403), `CASHIER_SESSION_OPEN` (409), `STAFF_HAS_OPEN_REGISTER_SESSION` (409). Reused
`BRANCH_NOT_FOUND`, `DUPLICATE_BRANCH_CODE`, `REGISTER_SESSION_ALREADY_OPEN`.

**Register-session constraints:** `IX_RegisterSessions_RegisterId_Open` (existing) +
`IX_RegisterSessions_OpenedByUserId_Open` (new), both filtered-unique on `Status = Open`;
races translated to typed conflicts.

**Force-close behavior:** `ForceCloseAsync` runs the identical reconciliation as `CloseAsync`
(expected = opening + cash-in − cash-out), no ownership check, no active-branch requirement;
`session.Close(actingUserId, …)` so `ClosedByUserId` = the admin and `OpenedByUserId` is never
touched. Session row is retained.

---

## 3. Frontend

- **`/branches`** — `BranchesPage` (`DashboardLayout`, `Table`, search + status filter),
  `BranchFormModal` (create: name/code/address; edit: code read-only), `BranchStatusBadge`.
  `RequireCapability capability="branch:manage"` on the route; nav item gated on
  `branch:manage`. Deactivate `ConfirmDialog` now names the assigned-staff count.
- **Staff branch UI** — Branch column on `StaffPage`; Branch field on `InviteStaffModal`
  (shown for branch-scoped roles) and `ChangeRoleModal` (shown when promoting to a scoped
  role); new `ChangeBranchModal` ("Change branch" row action for scoped members).
- **POS context** — `PosPage` rewritten around `posApi.context()`.
  - Branch-scoped user or single-branch Owner: no branch step.
  - Owner/Admin with ≥2 active branches: `BranchPicker` (choice persisted in
    `localStorage` `negosio.pos.branch.v1.<tenant>`; "Switch branch" clears it).
- **`RegisterPicker`** — lists `posApi.registers(branchId)` rows: *Available* → `[Select]`;
  *In use by \<name\>* → disabled; *Your open session* → `[Continue]` (straight to terminal,
  no opening-cash prompt). `REGISTER_SESSION_ALREADY_OPEN` / 403 → picker re-renders with a
  notice.
- **Opening-cash screen** (`PosSessionGate`) — `[← Back to registers]` (→ POS RegisterPicker,
  no session created) and `[Exit POS]` (→ `/dashboard`). `CASHIER_SESSION_OPEN` surfaced inline.
- **Exit vs Close** — `PosShell` "Exit" is a plain link to `/dashboard`; the session stays
  open; re-entering `/pos` shows "Your open session — Continue". Only "Close session" (or
  admin force-close) reconciles + releases.
- **Cashier nav restrictions** — `/registers` route + nav gate moved `pos:operate` →
  `register:manage`; Reports placeholder gated on `register:manage` (**absent**, not greyed,
  for a Cashier); Dashboard "Manage registers" quick action gated on `register:manage`.
- **Force-close UI** — on `/registers`, `RegisterSessionCell` shows "Open by \<name\>" and a
  **Force close** action for Owner/Admin on another user's session (own session keeps the
  normal Close). `CloseSessionModal` gains a `force` variant → `POST …/force-close`, danger
  styling, a warning callout, identical reconciliation display. `register:force-close`
  capability mirrors the backend policy.
- **Sales branch filtering** — `SalesPage` gets a branch `Select` + Branch column when
  `branches.length > 1` (active + inactive listed for Owner/Admin). `SaleDetailPage` branch
  line is conditional on multi-branch. Numbers render as `#0000001` / "Sale #0000001" /
  "Return 0000002 · for sale 0000001".

---

## 4. Multi-Branch Correctness

All verified against the running API (script `verify-phase5-api.mjs`, 49/49 assertions) and,
where UI-relevant, in the browser (`verify-phase5-ui.mjs` + screenshots
`p5-01`…`p5-10`).

| Property | Result |
|---|---|
| Inventory isolation | Branch A opening 10 → sell 1 → return 1 ⇒ **10**; Branch B opening 4 → sell 2 ⇒ **2**. Foreign-branch adjust as InventoryStaff → 403 `BRANCH_FORBIDDEN`. |
| Stock-movement isolation | movements list forced to the scoped user's branch (`ResolveListFilterAsync`). |
| Register isolation | `/api/registers` list + `/api/pos/registers` return only the resolved branch; scoped user passing a foreign `branchId` to `/api/pos/registers` → 403. |
| Session isolation | one open session per register (2nd open → `REGISTER_SESSION_ALREADY_OPEN`); one per user (2nd register → `CASHIER_SESSION_OPEN`). |
| POS branch isolation | cashier `/api/pos/context` → their branch, `canPickBranch:false`; `/api/pos/catalog?branchId=<foreign>` → 403; checkout `{branchId:<foreign>}` → 403. |
| Cart storage isolation | cart + attempt keys keyed `tenant/branch/register/session` (unchanged); branch key `negosio.pos.branch.v1.<tenant>` added for all-branch operators only. |
| Checkout idempotency isolation | `clientRequestId` uniqueness stays `(TenantId, ClientRequestId)`; unaffected by branch. |
| Sales ownership | cashier `/api/sales` → only their branch even with `?branchId=<foreign>`; foreign `/api/sales/{id}` and `/receipt` → **404**. Owner sees both branches. |
| Returns / restock ownership | return derived from the sale's branch; restock to that branch; a scoped user returning a foreign sale → 404; **Owner can return against an inactive branch** and the restock lands there (Branch B 2 → 3 after an inactive-branch return). |
| 7-digit per-branch numbering | fresh Branch A: Sale `0000001`; fresh Branch B: Sale `0000001`; Branch A Return `0000002`; Branch B Sale `0000002`. `/^\d{7}$/` on both sale and return numbers. UI shows `#0000001` in the Sales list, "Sale #…" on detail/success, "Return 0000002 · for sale …" in the returns list. No `\d{8}` assertions remain in tests/fixtures/UI (barcodes like `4800000000001` untouched). |

---

## 5. Security

| Check | Result |
|---|---|
| `BRANCH_FORBIDDEN` | scoped user targeting a foreign branch on inventory-adjust / pos-catalog / checkout / pos-registers → 403. |
| `BRANCH_INACTIVE` at login | Cashier/Manager/InventoryStaff assigned to an inactive branch → `400 BRANCH_INACTIVE` with the friendly message. |
| `BRANCH_INACTIVE` on an existing JWT | Cashier logged in before deactivation → next authenticated request → **401 `BRANCH_INACTIVE`** (no wait for expiry). |
| branch-scoped staff restrictions | `/api/staff`, `POST /api/branches`, `PUT /api/settings/tax` → 403 for a Cashier. Invite with no branch for a scoped role → 400; Admin invite with a branch → `BRANCH_FORBIDDEN`. |
| Owner/Admin all-branch | login unaffected by inactive branches; can view historical inactive-branch sales/detail/receipt; can reassign staff; can force-close. |
| session ownership | checkout / normal-close through another user's session → `403 SESSION_NOT_OWNED`. |
| one-session-per-register / -per-user | `REGISTER_SESSION_ALREADY_OPEN` / `CASHIER_SESSION_OPEN`. |
| force-close Owner/Admin only | Manager (has `RegisterManage`) → `403` at `POST …/force-close`; Owner → 200, `OpenedByUserId` preserved, `ClosedByUserId` = Owner, reconciliation persisted. |
| foreign Sale Detail hiding | `404 SALE_NOT_FOUND` (not 403) for a scoped user's foreign sale/receipt. |
| cost redaction (Phase 2/4 regression) | Cashier `GET /api/products/{id}` → every variant `costPrice` null. |

---

## 6. Tests

```
Unit:         88 passed / 0 failed / 0 skipped   (baseline 76)
Integration: 162 passed / 0 failed / 0 skipped   (baseline 114)
```

Major added / changed integration suites (`tests/Negosio.IntegrationTests/Branches/`):

- `BranchManagementTests` — CRUD, `DUPLICATE_BRANCH_CODE`, authz matrix (Owner/Admin ✓,
  Manager/Cashier/InventoryStaff/Viewer ✗), tenant isolation (cross-tenant → 404), update
  keeps code, deactivate + `includeInactive`, `LAST_ACTIVE_BRANCH`, reactivate.
- `BranchAuthTests` — login gate (active ✓ / inactive `BRANCH_INACTIVE` for
  Cashier/Manager/InventoryStaff), Owner/Admin unaffected, existing-JWT lockout, reactivate,
  reassign restores.
- `BranchScopedAccessTests` — inventory list forced to branch, foreign adjust → 403, sales
  scoped + foreign 404, checkout/catalog foreign → 403, Owner sees all, sell-one-branch
  isolation, **per-branch `0000001` numbering**, scoped `GET /api/branches`.
- `RegisterSessionModelTests` — one-per-register, one-per-user, checkout/close ownership,
  Manager force-close → 403 + Owner force-close preserves ownership + reconciliation,
  force-close on an inactive branch, `GET /api/registers` exposes the open session.
- `StaffBranchTests` — invite with active/inactive/no branch, Admin-with-branch rejected,
  role→branch reconciliation (promote needs a branch, demote clears it), change-branch
  immediate effect, change-branch blocked by an open session.
- `PosContextTests` — context (`canPickBranch` for cashier / 1-branch owner / 2-branch owner),
  register availability + `mine` flag, foreign branch → 403.

Unit: `BranchDomainTests`, `BranchRolesTests`; `DocumentNumberService` / `SaleNumber`
assertions moved to `\d{7}` / `0000001…`.

Test infra: `CreateBranchAsync`, `AddLoginableTenantUserAsync`,
`AddTenantUserTokenAsync(..., branchId)` (auto-binds branch-scoped roles), `RawLoginAsync`.

---

## 7. Browser Verification

Real API (`ASPNETCORE_ENVIRONMENT=Development`) + `npm run dev`, headless Chrome + CDP.
**Zero console errors across every screen.** Scenarios actually executed:

| # | Scenario | Result |
|---|---|---|
| Cashier nav | sidebar = Dashboard / Products / Categories / Stock levels / Stock movements / POS / Sales — **no Registers, Reports, Branches, Staff, Settings** | ✅ (screenshot `p5-01`, `p5-07`) |
| Dashboard quick actions | "Manage registers" absent for Cashier | ✅ |
| Direct `/registers` as Cashier | clean "You don't have access to this page" panel, no raw JSON | ✅ (`p5-02`, `p5-07`) |
| Cashier `/pos` | **no branch picker**; "Choose a register" / "BGC Branch" header | ✅ (`p5-03`) |
| Register picker states | *Available* `[Select]`, *In use by Bea Cashier* disabled (API + `p5-08`), *Your open session* `[Continue]` (`p5-06`) | ✅ |
| Opening-cash **Back to registers** | returns to the POS RegisterPicker (path `/pos`), **no session created** | ✅ (`p5-04`) |
| Opening-cash **Exit POS** | → `/dashboard`, no session created | ✅ |
| Open register | opening cash → terminal loads; session owner = the Cashier | ✅ (`p5-05`) |
| **Exit** from terminal | → `/dashboard`, **session stays open** (re-entering `/pos` shows "Your open session — Continue") | ✅ (`p5-06`) |
| **Continue** | terminal loads, **no opening-cash prompt** | ✅ |
| Session exclusivity | 2nd Cashier's `/api/pos/registers` shows the register "In use by V Cashier", `mine:false`; open → `REGISTER_SESSION_ALREADY_OPEN` | ✅ (API) |
| One session per Cashier | 2nd register open → `CASHIER_SESSION_OPEN` | ✅ (API) |
| Session ownership | checkout / close through another Cashier's session → `SESSION_NOT_OWNED` | ✅ (API) |
| Deactivate branch (session open) | branch → inactive, assignment kept, **session NOT auto-closed** | ✅ (API) |
| Existing-JWT lockout | `/api/auth/me` with the pre-deactivation token → `401 BRANCH_INACTIVE` | ✅ (API) |
| New-login lockout | `POST /api/auth/login` for the locked-out Cashier → `BRANCH_INACTIVE` | ✅ (API) |
| Owner still works | login OK; historical inactive-branch sales / detail / receipt available | ✅ (API) |
| **Force-close UI** | `/registers` shows "Open by Bea" + **Force close**; modal "Force close BGC POS 1 · opened with ₱1,500.00 by Bea Cashier" + warning + counted-cash field → force close → difference shown; backend: session Closed, register released, `OpenedBy` = Bea, `ClosedBy` = Owner | ✅ (`p5-08`, `p5-09`, `p5-10`) |
| Manager force-close restriction | `POST …/force-close` as Manager → `403` | ✅ (API + integration test) |
| Reactivate branch | Cashier login works again | ✅ (API) |
| Staff reassignment | `POST /api/staff/{id}/branch` BGC→MAIN → immediate; Cashier's `/api/branches` now returns the new branch | ✅ (API) |
| Open session blocks reassignment | → `409 STAFF_HAS_OPEN_REGISTER_SESSION`; succeeds after close | ✅ (API) |
| Multi-branch inventory isolation | sell/return in Branch A leaves Branch B untouched | ✅ (API) |
| Per-branch numbering | A `0000001` / B `0000001` / A return `0000002` / B `0000002`; UI `#0000001` | ✅ (API + `p5-06` Sales list) |
| Sales branch scope | Cashier `/api/sales` scoped; foreign detail/receipt → 404; Owner sees ≥2 branches | ✅ (API) |
| Return against inactive branch | Owner return succeeds; restock to the originating inactive branch | ✅ (API) |
| Dashboard scope | Owner: tenant-wide; scoped user: `branchCount == 1` | ✅ (API) |
| RBAC regression sweep (Owner) | `/dashboard /categories /products /inventory /inventory/movements /registers /pos /sales /settings /staff /branches` — all render, **0 console errors** | ✅ (`verify-phase5-ui` `owner_regression`) |
| Cost redaction | Cashier product detail → `costPrice` null | ✅ (API) |

Not run in a live browser session (covered by integration tests, noted here per the brief):
the Manager `/registers` view hiding the Force-close button (the `register:force-close`
capability = Owner/Admin is unit-mirrored and the API returns 403 for Manager); the concurrent
double-open race (the DB filtered-unique index + `CheckoutConcurrencyTests`-style coverage).

---

## 8. Git State

```
branch:            feature/branch-management
HEAD:              5d4b26a  (this docs commit will advance it by 1)
ahead of master:   18 commits  →  19 with this report
working tree:      clean
push status:       NOT pushed
merge status:      NOT merged into master
```

Commits (oldest → newest):

```
3a2ac4d docs: Phase 5 design — branch management + branch-scoped access
ef43b28 docs: Phase 5 implementation plan
2002e2b feat(branches): branch domain methods + management service + error codes
d9ac056 feat(branches): branch CRUD API + BranchManage policy + tests
bab5872 feat(branches): /branches page, form modal, capability-gated nav
ebfe61f feat(staff): User.BranchId + StaffInvitation.BranchId + backfill migration
d227e33 feat(branches): BranchAccessResolver + scoped GET /api/branches
3b4aaab feat(branches): enforce branch scope across inventory, registers, POS, sales, dashboard
3ca5e57 feat(auth): branch-scoped users require an active assigned branch to authenticate
bf15bbd feat(pos): 7-digit transaction numbers; cashier register-management lockout
358b3ae feat(pos): one open session per user, session ownership, owner/admin force-close
e70d11e feat(pos): branch context + register-availability endpoints
fb23de1 feat(staff): branch on invitation, role/branch reconciliation, change-branch
3052bff feat(web): POS branch flow, register-availability picker, staff branch UI, sales branch filter
98b48ed fix(web): gate the 'Manage registers' dashboard quick action on register:manage
b529a6f docs: Phase 5 progress status
fcb2da7 feat(registers): owner/admin force-close reconciliation UI on /registers
5d4b26a feat(branches): show assigned-staff count in the deactivate warning
```

**Do not merge.** Awaiting review.

---

## 9. Remaining Limitations (explicitly out of scope)

no multiple branch assignments per staff member · no stock transfers between branches · no
branch-specific tax · no branch-specific pricing · no branch-specific catalog · no
consolidated / cross-branch reporting · no dedicated branch analytics · no
operate-another's-session override (force-close only) · no Void · no Reporting phase · no
offline selling · no F&B vertical · no tenant timezone (server UTC) · PHP currency fixed · one
email = one Negosio tenant · no separate cashier PIN identity · **no production invitation
email provider** — outside Production the accept URL is returned in the API response + the
structured log; in Production it is withheld and a real provider is the next integration step.

---

## Working dev logins (`Negosio.InvUITest_MAIN_204CD2A1`)

| Role | Email | Password | Branch |
|---|---|---|---|
| Owner | `invui@example.com` | `SecurePassword123!` | all |
| Cashier | `bea@negosio.test` | `Cashier123!` | BGC Branch |
| Cashier | `bgc.cashier@negosio.test` | `Cashier123!` | BGC Branch |
| Cashier | `cashier@negosio.test` | `Cashier123!` | Main Branch |

Verification also created disposable `Verify A / Verify B` branches + `ca.*@ex.com` /
`mgr.*@ex.com` accounts in this tenant — harmless test residue.
