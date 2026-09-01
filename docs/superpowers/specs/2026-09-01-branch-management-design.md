# Phase 5 — Branch Management & Branch-Scoped Access — Design

**Status:** approved design (user-locked rules across three briefs, 2026-09-01)
**Branch:** `feature/branch-management` (off `master` @ `61d2b01`)
**Do NOT auto-merge.** Stop after implementation + full verification + final report.

---

## 1. Purpose & scope

Make the existing multi-branch architecture reachable, and lock branch-scoped users to
one branch across the whole system.

Two things ship together:

1. **Branch Management v1** — list / view / add / edit / activate / deactivate branches
   (no delete). Owner/Admin only.
2. **Branch-scoped access** — every non-Owner/Admin user is bound to exactly one branch;
   role says *what* they may do, branch assignment says *where*, branch active-status says
   *whether they may use Negosio at all*.

### Explicitly NOT in this phase

Stock transfers · branch-specific tax / pricing / catalog · consolidated/branch reporting ·
branch-level dashboards beyond scoping the existing metrics · warehouses / departments /
regions · Void · Reporting phase · F&B · offline selling · multi-currency · tenant timezone ·
email/PDF/thermal receipts · Azure · Redis · microservices · a manager/owner override to
*operate* another user's open session (only force-close ships).

### Preserved limitations (carry into the final report)

no stock transfers · no branch-specific tax · no branch-specific pricing · no consolidated
branch reporting · no Void · no Reporting phase · no offline selling · no F&B · no tenant
timezone · PHP currency fixed · one email = one tenant · no separate cashier PIN · no
operate-another's-session override (force-close only).

---

## 2. The security model (locked)

```
ROLE / CAPABILITY   → WHAT the user may do
BRANCH ASSIGNMENT   → WHERE the user may do it
BRANCH ACTIVE       → WHETHER a branch-scoped user may access Negosio at all
```

**Branch-scoped roles:** `Manager, Cashier, InventoryStaff, KitchenStaff, Viewer`
→ `User.BranchId != null` (hard invariant).

**All-branch roles:** `Owner, Admin`
→ `User.BranchId == null` (hard invariant). Tenant-wide access.

A branch-scoped request is allowed only if **all** hold:

```
account active
AND branch assigned (BranchId != null)
AND assigned branch active
AND capability/role allows the action
AND the requested resource belongs to the assigned branch
```

Owner/Admin: `account active AND capability allows action`, tenant-wide.

Frontend hiding is UX only. The backend is authoritative on every rule.

---

## 3. Data model changes

### 3.1 `Branch` (tenant DB) — no schema change

`Branch` already has `Id, TenantId, Name, Code, AddressLine1, AddressLine2, City, Province,
PostalCode, IsActive, CreatedAtUtc, UpdatedAtUtc` and a unique index
`IX_Branches_TenantId_Code`. Add **domain methods only**:

- `UpdateDetails(name, addressLine1, addressLine2?, city, province, postalCode?)` — trims,
  re-applies the same normalization as `Create`, `Touch()`. **No `code` parameter — code is
  immutable after creation.**
- `Deactivate()` → `IsActive = false; Touch()`.
- `Reactivate()` → `IsActive = true; Touch()`.

`Create` already uppercases `Code` and rejects blank name/code.

### 3.2 `User.BranchId` (tenant DB) — **migration**

- Add `Guid? BranchId` to `User`. FK → `Branches.Id`, `OnDelete(DeleteBehavior.Restrict)`.
- Index `IX_Users_BranchId`.
- Domain: `User.AssignBranch(Guid branchId)` (sets + `Touch()`), `User.ClearBranch()`
  (sets null + `Touch()`). `User.Create` gains an optional `Guid? branchId` parameter
  (default null); `ChangeRole` does **not** touch branch (the service reconciles — see §7).
- **Backfill in the migration:** every existing `User` whose role is branch-scoped gets
  `BranchId` = the tenant's single branch. Raw SQL in the migration `Up()`:
  `UPDATE Users SET BranchId = (SELECT TOP 1 Id FROM Branches) WHERE [Role] NOT IN (1, 2)`
  (`UserRole.Owner = 1`, `Admin = 2`; every current tenant has exactly one branch).
  Owner/Admin stay null.

### 3.3 `StaffInvitation.BranchId` (platform DB) — **migration**

- Add `Guid? BranchId` to `StaffInvitation` (platform DB — no FK, `Branches` live in the
  tenant DB; it is a carried value validated at invite and accept time).
- `StaffInvitation.Create` gains `Guid? branchId`. It does **not** enforce the
  role↔branch rule itself (needs tenant-DB knowledge) — `StaffService` does, before calling
  `Create`.

### 3.4 `RegisterSession` — **migration** (one-open-session-per-user)

- New filtered unique index
  `IX_RegisterSessions_OpenedByUserId_Open` on `(TenantId, OpenedByUserId)`
  `WHERE [Status] = 0` (Open). Mirrors the existing per-register
  `IX_RegisterSessions_RegisterId_Open`.
- No entity change — `OpenedByUserId` / `ClosedByUserId` already exist. `Close` already
  records `ClosedByUserId` distinct from `OpenedByUserId`.

**Migrations total: 2.** One tenant-DB migration (`User.BranchId` + backfill +
`IX_Users_BranchId` + `IX_RegisterSessions_OpenedByUserId_Open`), one platform-DB migration
(`StaffInvitation.BranchId`). Generated with the existing
`dotnet dotnet-ef migrations add` invocations (see `handover.md` / memory for the exact
context + output-dir flags).

---

## 4. `BranchAccessResolver` — the enforcement core

New scoped Application service `Negosio.Application.Branches.BranchAccessResolver`
(`IBranchAccessResolver`), ctor deps `ITenantDbContext`, `ICurrentUser`. Memoizes the
current user's row for the request.

```csharp
public interface IBranchAccessResolver
{
    /// All-branch role: null (caller may see every branch).
    /// Branch-scoped role: the assigned branch id (never null for a valid account).
    Task<Guid?> AssignedBranchIdAsync(CancellationToken ct = default);

    /// List/read filter. All-branch: returns `requested` (may be null = all branches).
    /// Branch-scoped: returns the assigned branch, ignoring `requested`.
    Task<Guid?> ResolveListFilterAsync(Guid? requested, CancellationToken ct = default);

    /// Write/target branch. All-branch: `requested` (required), validated to exist.
    /// Branch-scoped: the assigned branch; throws BRANCH_FORBIDDEN if `requested` is set
    /// and differs. Then asserts the resolved branch is Active (unless `allowInactive`).
    Task<Guid> ResolveTargetBranchAsync(Guid? requested, bool allowInactive = false, CancellationToken ct = default);

    /// True for Owner/Admin.
    bool IsAllBranch { get; }
}
```

Role set lives in `Negosio.Application.Branches.BranchRoles`:
`BranchScoped = { Manager, Cashier, InventoryStaff, KitchenStaff, Viewer }`,
`IsAllBranch(role) => role is Owner or Admin`.

New error codes (`ErrorCodes`):

| code | meaning | HTTP |
|---|---|---|
| `BRANCH_INACTIVE` | assigned branch (or target branch) is inactive | 401 at auth, 409 at write |
| `BRANCH_FORBIDDEN` | branch-scoped user targeted a branch that is not theirs | 403 |
| `LAST_ACTIVE_BRANCH` | cannot deactivate the tenant's last active branch | 409 |
| `SESSION_NOT_OWNED` | operating/closing a session owned by someone else | 403 |
| `CASHIER_SESSION_OPEN` | user already has an open session | 409 |
| `STAFF_HAS_OPEN_REGISTER_SESSION` | cannot change branch while user owns an open session | 409 |

Reuse existing `BRANCH_NOT_FOUND`, `DUPLICATE_BRANCH_CODE`, `REGISTER_SESSION_ALREADY_OPEN`.

---

## 5. Authentication — branch-active enforcement (CRITICAL, locked)

A branch-scoped user may authenticate and keep using Negosio **only while their assigned
branch is active**. Applies to new logins *and* already-issued JWTs.

### 5.1 Login — `AuthService.LoginAsync`

After the existing `login.IsActive` / `tenant.IsOperational` checks and after opening
`tenantDb` (it already does, to build the user DTO): if `BranchRoles.IsAllBranch(login.Role)`
is false, load `User.BranchId` + the branch's `IsActive` in one query:

```
if branch-scoped:
    if user.BranchId is null            -> BusinessRuleException(BRANCH_INACTIVE, <msg>)  // data-integrity guard
    if branch not found / wrong tenant  -> BusinessRuleException(BRANCH_INACTIVE, <msg>)
    if !branch.IsActive                 -> BusinessRuleException(BRANCH_INACTIVE, <msg>)
```

`<msg>` = `"Your assigned branch is currently inactive. Please contact your administrator."`
Never a 500. Owner/Admin skip this entirely.

### 5.2 Per authenticated request — `BranchAccessMiddleware`

New middleware registered in `Program.cs` **immediately after**
`app.UseMiddleware<TenantResolutionMiddleware>()` (line 67) and before
`app.UseAuthorization()`. It has the routed `ITenantDbContext`.

```
if not authenticated            -> next()          // anonymous / auth endpoints
if role is all-branch           -> next()
// branch-scoped:
load user.BranchId + branch.IsActive via ITenantDbContext (PK-indexed, AsNoTracking)
if BranchId null OR branch missing OR !IsActive:
    write ApiError envelope { code: BRANCH_INACTIVE, message: <msg> }, status 401
    short-circuit (do NOT call next)
else next()
```

401 (not 403): the session is no longer usable; the SPA's existing 401 handler clears it and
routes to `/login`, where a fresh login attempt surfaces the same `BRANCH_INACTIVE` message
from §5.1. No token blacklist, no Redis — one indexed tenant-DB query per branch-scoped
request, matching the Phase 4 platform freshness-check strategy. `OnTokenValidated`
(Phase 4) is unchanged: it stays a pure platform-DB check (account active + role-claim
match).

Cost note: a branch-scoped request now does 1 platform query (`OnTokenValidated`) + 1 tenant
query (this middleware). Owner/Admin: 1 platform query only.

### 5.3 What deactivation does NOT do

Branch `Deactivate()` only flips `IsActive`. It never moves users, clears `BranchId`,
deactivates accounts, closes sessions, or deletes anything. Recovery = reactivate the branch
**or** reassign the user (§7.3).

---

## 6. Branch Management API

`BranchesController` (`api/branches`):

| Method | Route | Policy | Notes |
|---|---|---|---|
| GET | `/api/branches?includeInactive=` | `[Authorize]` | see §6.1 |
| GET | `/api/branches/{id}` | `BranchManage` | full detail; 404 cross-tenant |
| POST | `/api/branches` | `BranchManage` | create |
| PUT | `/api/branches/{id}` | `BranchManage` | name + address; **not** code |
| POST | `/api/branches/{id}/deactivate` | `BranchManage` | last-active guard |
| POST | `/api/branches/{id}/reactivate` | `BranchManage` | |

`AuthorizationPolicies.BranchManage` = `RequireClaim(role, [Owner, Admin])` — same shape as
`StaffManage`. `GET` list stays on the fallback `[Authorize]`.

### 6.1 `GET /api/branches` behaviour

- **Owner/Admin:** `includeInactive=false` → active branches; `includeInactive=true` →
  all branches. Full projection (see DTO below).
- **Branch-scoped user:** always exactly `[their assigned branch]`, `includeInactive`
  ignored. (While authenticated they necessarily have an active assigned branch — §5.)
  Never exposes any other branch.

### 6.2 Contracts (`Negosio.Application.Branches.BranchContracts`)

```csharp
public sealed record BranchDto(
    Guid Id, string Name, string Code,
    string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode,
    bool IsActive, DateTime CreatedAtUtc);

public sealed record CreateBranchRequest(
    string Name, string Code,
    string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode);

public sealed record UpdateBranchRequest(
    string Name,
    string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode);
```

Keep `IBranchQueryService` for the read feed (widen its projection + honour §6.1); add
`IBranchManagementService` (`GetAsync`, `CreateAsync`, `UpdateAsync`, `DeactivateAsync`,
`ReactivateAsync`) implemented by `BranchManagementService`
(`ITenantDbContext`, `ICurrentUser`, validators).

`BranchValidators`: `CreateBranchRequestValidator` (Name NotEmpty ≤150; Code NotEmpty ≤20,
matches `^[A-Za-z0-9-]+$`; AddressLine1/City/Province NotEmpty with the existing max lengths;
AddressLine2 ≤200; PostalCode ≤20), `UpdateBranchRequestValidator` (same minus Code).

### 6.3 Service rules

- **Create:** normalize via `Branch.Create`; translate the unique-index violation →
  `ConflictException(DUPLICATE_BRANCH_CODE, "A branch with this code already exists.")`
  (same pattern as `RegisterService.SaveOrTranslateAsync`).
- **Update:** load in-tenant (404 otherwise) → `UpdateDetails(...)` → save.
- **Deactivate:** load in-tenant (404). If it is the only `IsActive` branch for the tenant
  → `BusinessRuleException(LAST_ACTIVE_BRANCH, "You can't deactivate the last active
  branch.")`. Otherwise `Deactivate()` + save. **Does not** cascade.
- **Reactivate:** load in-tenant (404) → `Reactivate()` + save. No guard.
- Every method resolves tenant from `ICurrentUser` and filters `TenantId` — a cross-tenant
  id is a 404.

---

## 7. Staff ↔ branch (Phase 4 changes)

### 7.1 Invitation

`InviteStaffRequest` gains `string? BranchId`. `StaffService.InviteAsync`:

- Resolve target role. If **branch-scoped**: `BranchId` required; the branch must exist in
  the tenant DB, belong to the tenant, and be **active** — else
  `ValidationAppException` / `BusinessRuleException(BRANCH_INACTIVE, "You can't invite
  someone into an inactive branch.")`.
- If **Owner/Admin** (only Admin is inviteable — Owner still rejected by the Phase 4 rule):
  `BranchId` must be null/absent.
- Persist `StaffInvitation.Create(..., branchId)`.

`InvitationPreviewDto` gains `string? BranchName` (nice-to-have on the accept screen).
`StaffInvitationService.AcceptAsync` / `CreateTenantUserAsync` thread `invitation.BranchId`
into `User.Create(..., branchId)`.

### 7.2 Role change — backend invariants (`StaffService.ChangeRoleAsync`)

`ChangeStaffRoleRequest` gains `string? BranchId`.

| transition | rule |
|---|---|
| all-branch → branch-scoped (e.g. Admin→Manager) | `BranchId` **required**, must be active. Sets role + `User.AssignBranch`. |
| branch-scoped → branch-scoped (e.g. Cashier→Manager) | keep the current branch unless a different valid active `BranchId` is supplied. |
| branch-scoped → all-branch (e.g. Manager→Admin) | ignore/reject `BranchId`; `User.ClearBranch()`. |
| Owner rules | unchanged from Phase 4 (Owner never assignable, Owner's role never changes). |

Applied on **both** the platform `PlatformUserLogin.ChangeRole` and the tenant
`User.ChangeRole` + branch mutation, in the existing platform-first order.

### 7.3 Change branch — new `StaffService.ChangeBranchAsync(userId, ChangeStaffBranchRequest)`

`ChangeStaffBranchRequest(string BranchId)`. Owner/Admin only (`StaffManage`).

- Target user must be branch-scoped (else `BusinessRuleException` — "Owners and admins
  aren't assigned to a branch.").
- New branch must exist in tenant + be **active**.
- **Reject if the user owns an open register session** →
  `ConflictException(STAFF_HAS_OPEN_REGISTER_SESSION, "Close or force-close this person's
  open register session before moving them.")`.
- `User.AssignBranch(newBranchId)` + save. Takes effect on the user's next request (branch
  is not in the JWT — no re-login needed).

### 7.4 Staff list + `StaffMemberDto`

`StaffMemberDto` gains `Guid? BranchId, string? BranchName`. `StaffService.ListAsync`
joins the branch name. Invitations carry their `BranchId`/name too.

---

## 8. Branch-scoped enforcement across every feature

For each area: **reads** use `ResolveListFilterAsync`, **writes** use
`ResolveTargetBranchAsync`. Owner/Admin keep full range; branch-scoped users are forced to
their branch and get `BRANCH_FORBIDDEN` (403) / `404` on a foreign target.

| Area | Endpoint(s) | Change |
|---|---|---|
| Dashboard | `GET /api/dashboard` | `DashboardService` filters today's-sales / transactions / avg / low-stock by `ResolveListFilterAsync(null)`. `branchCount` → **active branches**; for a scoped user it is `1`. |
| Inventory list | `GET /api/inventory` | `query.BranchId = await ResolveListFilterAsync(query.BranchId)`. |
| Inventory adjust / opening stock | `POST /api/inventory/adjustments` | `branchId = await ResolveTargetBranchAsync(request.BranchId)` (must be active). |
| Stock movements | `GET /api/inventory/movements` | `ResolveListFilterAsync`. |
| Registers list | `GET /api/registers` | `ResolveListFilterAsync`. |
| Register create | `POST /api/registers` | `ResolveTargetBranchAsync(request.BranchId)` — replaces the existing existence check; also asserts active. (RegisterManage = Owner/Admin/**Manager**; a scoped Manager can only create in their own branch.) |
| Register session open | `POST /api/register-sessions/open` | resolve via the register's `BranchId`; `BRANCH_FORBIDDEN` if it is not the caller's; assert branch active. Then the session rules in §9. |
| POS catalog search / barcode | `POST /api/pos/catalog`, `/barcode` | `ResolveTargetBranchAsync(branchId)`. |
| Checkout | `POST /api/pos/checkout` | `ResolveTargetBranchAsync(request.BranchId)`; existing session-branch match stays; add §9 ownership check. |
| Sales list | `GET /api/sales` | `query.BranchId = await ResolveListFilterAsync(query.BranchId)`. |
| Sale detail / receipt | `GET /api/sales/{id}`, `/receipt` | after loading, if scoped and `sale.BranchId != assigned` → **404** (`SALE_NOT_FOUND`) — do not leak existence. |
| Returns | `POST /api/sales/{id}/returns` | scoped user: `sale.BranchId` must equal assigned (else 404) **and** role has `RefundManage`. Restock still goes to `sale.BranchId`. **No active-branch guard** — Owner/Admin must be able to refund against an inactive historical branch. |

`ReturnService` keeps deriving branch from the sale; it gains only the scoped-user
sale-ownership check (Owner/Admin unrestricted).

---

## 9. Register-session model (locked)

### 9.1 Invariants (both DB-enforced)

- **One open session per register** — existing `IX_RegisterSessions_RegisterId_Open`.
- **One open session per user** — new `IX_RegisterSessions_OpenedByUserId_Open` (§3.4).

`RegisterSessionService.OpenAsync` catches `SqlUniqueViolation` and translates by constraint
name:

- register index → `ConflictException(REGISTER_SESSION_ALREADY_OPEN, "This register is
  already in use.")` (keep the existing code; message may say "in use").
- user index → `ConflictException(CASHIER_SESSION_OPEN, "You already have an open register
  session. Continue or close it first.")`.

### 9.2 Ownership

- **Checkout** (`CheckoutService`): after loading the session, if
  `session.OpenedByUserId != _currentUser.UserId` →
  `ForbiddenAppException(SESSION_NOT_OWNED, "This register session belongs to another
  user.")`. Applies to **everyone**, Owner/Admin included (no operate-through override this
  phase).
- **Normal close** (`RegisterSessionService.CloseAsync`): same ownership check.
- **`GetCurrentAsync`**: the "your open session" lookup filters `OpenedByUserId ==
  _currentUser.UserId`. (For the picker's "in use by X", see §10.)

### 9.3 Force-close — `POST /api/register-sessions/{id}/force-close`

- New policy `AuthorizationPolicies.RegisterForceClose` = `[Owner, Admin]` **only** — not
  Manager, even though Manager has `RegisterManage`. Focused administrative override.
- Body: `CloseRegisterSessionRequest` (same counted-cash payload).
- Runs the **same reconciliation** as `CloseAsync` (expected cash from sales/refunds,
  difference recorded).
- `OpenedByUserId` unchanged (original cashier); `ClosedByUserId` = the acting Owner/Admin.
- Owner/Admin may force-close a session on **any** branch, including an inactive one (needed
  for the deactivation-cleanup path). A scoped user can never reach this endpoint.
- Not subject to §9.2 ownership.

### 9.4 Exit ≠ Close

No backend behaviour — documented for the frontend (§10). Browser close / refresh / logout /
"Exit" never close a session. Only `close` / `force-close` do.

---

## 10. Frontend

### 10.1 Branch Management — `/branches`

- `pages/BranchesPage.tsx` — `DashboardLayout`, `Table`, `SearchInput`, status `Select`
  (All / Active / Inactive → `includeInactive`), `useQuery(['branches','manage', filters])`.
  Columns: Name, Code, City/Province, Status badge, actions (Edit / Deactivate / Reactivate).
  `ConfirmDialog` for lifecycle; the deactivate dialog notes "Staff assigned here lose
  access until you reassign them or reactivate the branch." `LAST_ACTIVE_BRANCH` →
  toast the server message.
- `components/branches/BranchFormModal.tsx` — create (name, code, address fields) / edit
  (code rendered read-only with a hint "Branch codes can't be changed."). Reuses `Modal`,
  `TextField`, `Select`, `fieldErrorsFrom`.
- `components/branches/BranchStatusBadge.tsx` (Active → success, Inactive → neutral).
- `api/branches.ts` — move `branchesApi.list` here from `api/inventory.ts` (re-export for
  compatibility), add `list({ includeInactive })`, `get`, `create`, `update`, `deactivate`,
  `reactivate`.
- `api/types.ts` — widen `BranchDto`; add `CreateBranchRequest`, `UpdateBranchRequest`.
- `lib/useCan.ts` — add `'branch:manage'` → `['Owner','Admin']`.
- `lib/nav.ts` — `{ label: 'Branches', icon: Building2, to: '/branches', enabled: true,
  capability: 'branch:manage' }` in the Staff/Settings group.
- `App.tsx` — `/branches` wrapped in `<RequireCapability capability="branch:manage"
  title="Branches">`.

### 10.2 Inventory / Registers / Movements / Sales

- **Inventory, Registers, Movements** — no code change. A branch-scoped user's
  `branchesApi.list` returns one branch → the existing `multiBranch = branches.length > 1`
  gate hides the selector and the data is already branch-correct (backend forces it).
  Owner/Admin with >1 branch keep the existing selectors.
- **Sales** — add the same `multiBranch` branch `Select` (active + inactive, so historical
  inactive-branch sales stay filterable for Owner/Admin) wired to the existing `branchId`
  query param via `usePagedQuery`. Hidden for scoped users and single-branch tenants.
- **Sale detail** — gate the existing branch line on `multiBranch`.

### 10.3 POS — `pages/PosPage.tsx`

New `GET /api/pos/context` → `{ branchId, branchName, canPickBranch, branches: BranchDto[] }`:

- **canPickBranch = false** (branch-scoped, or Owner/Admin with exactly one active branch):
  branch is fixed. No `BranchPicker`.
- **canPickBranch = true** (Owner/Admin, ≥2 active branches): render `BranchPicker`
  (`components/pos/BranchPicker.tsx`, mirrors `RegisterPicker`); persist the choice in
  `localStorage` (`posStorage.readBranch/writeBranch`, key `negosio.pos.branch.v1.<tenant>`)
  — **only for all-branch users**.

Then `GET /api/pos/registers?branchId=<resolved>` → `[{ id, name, code, isActive,
openSession: { openedByUserId, openedByName, mine } | null }]` (branchId forced server-side
for scoped users). Reworked `RegisterPicker`:

- `openSession == null` → **Available**, selectable → OpenSession modal.
- `openSession.mine` → **"Your open session — Continue"**, selectable → straight to terminal.
- `openSession && !mine` → **"In use by {openedByName}"**, disabled, still visible.

`PosShell` header shows the branch name when `canPickBranch` or >1 active branch. A "Switch
branch" affordance appears only for all-branch users, only at the picker/gate stage, never
with an open cart — it clears the branch + register `localStorage` and returns to
`BranchPicker`.

`OpenSessionModal` (opening cash) and the terminal are unchanged. Cart/attempt keys already
include `branchId` — switching branch naturally leaves the other branch's cart untouched.

**Errors surfaced cleanly:** `CASHIER_SESSION_OPEN` → callout on the picker with a Continue
link; `REGISTER_SESSION_ALREADY_OPEN` → refetch the picker (someone took it);
`SESSION_NOT_OWNED` → return to picker; `BRANCH_INACTIVE` (mid-session) → the global 401
handler logs out.

### 10.4 Staff page

- `StaffPage.tsx` — Branch column; row actions gain "Change branch" for branch-scoped
  members (Owner/Admin actor). `ChangeBranchModal` (branch `Select`, active branches only).
- `InviteStaffModal` — when the chosen role is branch-scoped, show a required Branch
  `Select` (active branches). Hidden for Admin.
- `ChangeRoleModal` — when switching an unscoped member to a scoped role, show the Branch
  `Select` (required); when switching to Admin, no branch field.
- `api/staff.ts` / `types.ts` — `branchId` on invite / change-role, new `changeBranch`.

### 10.5 Dashboard

No layout change. Metrics already come scoped from the backend (§8). The "Branches" card
shows the active count; for a scoped user that is `1` (acceptable — no special-casing).

---

## 11. Tests

Baseline: **76 unit / 114 integration**. Report actual finals.

### 11.1 Integration — `tests/Negosio.IntegrationTests/Branches/`

**`BranchManagementTests`**
- Owner creates a branch → active, listed, `GET /{id}` detail correct.
- Duplicate code → `DUPLICATE_BRANCH_CODE` (409).
- Authz matrix on `POST /api/branches`: Owner ✓, Admin ✓, Manager ✗, Cashier ✗,
  InventoryStaff ✗, Viewer ✗ (403).
- Tenant isolation: tenant A's branch id → tenant B `GET/PUT/deactivate/reactivate` → 404.
- Update changes name/address, leaves code; code field in the request is ignored/rejected.
- Deactivate one of two → succeeds; still returned with `includeInactive=true`.
- Last active branch → deactivate → `LAST_ACTIVE_BRANCH` (409); branch stays active.
- Reactivate → active again.

**`BranchAuthTests`** (login + per-request)
- Cashier assigned an **active** branch → login succeeds; `/api/auth/me` works.
- Cashier assigned an **inactive** branch → login → `BRANCH_INACTIVE`.
- Manager assigned inactive branch → login → `BRANCH_INACTIVE`.
- Owner / Admin with inactive branches in the tenant → login succeeds.
- Cashier logs in (branch active) → Owner deactivates the branch → Cashier's **existing
  token** on any authed endpoint → 401 `BRANCH_INACTIVE` (no wait for expiry).
- Deactivate → Cashier denied → reactivate → Cashier login succeeds again.
- Cashier assigned inactive branch → Owner reassigns to an active branch → Cashier login
  succeeds, sees only the new branch.

**`BranchScopedAccessTests`**
- Cashier assigned BGC, tenant also has MAIN with stock:
  - `GET /api/inventory` → only BGC rows.
  - `POST /api/inventory/adjustments` with `branchId = MAIN` → 403 `BRANCH_FORBIDDEN`.
  - `GET /api/sales?branchId=MAIN` → only BGC sales (filter forced).
  - `GET /api/sales/{mainSaleId}` → 404.
  - `POST /api/pos/checkout` with `branchId = MAIN` → 403.
  - `POST /api/pos/catalog` with `branchId = MAIN` → 403.
  - `GET /api/branches` → exactly `[BGC]`.
- Owner: `GET /api/inventory` → MAIN + BGC rows; adjust either branch → ok.
- Multi-branch inventory isolation: MAIN opening 10, BGC opening 3, sell 2 at MAIN →
  MAIN 8, BGC 3.
- Per-branch numbering: MAIN sale `00000001`, BGC sale `00000001`, MAIN return `00000002`,
  BGC sale `00000002`.
- Returns/restock: sale at MAIN → return → MAIN restock only, BGC unchanged; still works
  after MAIN is deactivated (Owner acting).

**`RegisterSessionModelTests`**
- One open session per register: two opens on the same register → one 200, one
  `REGISTER_SESSION_ALREADY_OPEN`.
- One open session per user: same user opens a second register → `CASHIER_SESSION_OPEN`.
- Checkout through another user's session → 403 `SESSION_NOT_OWNED`.
- Normal close of another user's session → 403.
- Force-close: Manager → 403; Owner → 200, reconciliation persisted, `OpenedByUserId`
  unchanged, `ClosedByUserId` = Owner.
- Open session cleanup: Cashier opens BGC/POS1 → Owner deactivates BGC → Cashier request
  → 401 → Owner force-closes POS1 → reconciliation persisted.
- `ChangeBranchAsync` while the user owns an open session → `STAFF_HAS_OPEN_REGISTER_SESSION`.

**`StaffBranchTests`** (extends Phase 4 `StaffTests`)
- Invite Cashier with an active branch → accept → `User.BranchId` set; login → scoped.
- Invite Cashier with an inactive branch → 400/409 `BRANCH_INACTIVE`.
- Invite Cashier with no branch → validation error.
- Invite Admin with a branch → rejected.
- Change role Admin→Manager without a branch → validation error; with an active branch → ok,
  `BranchId` set.
- Change role Manager→Admin → `BranchId` cleared.
- `ChangeBranchAsync` MAIN→BGC (no open session) → ok, next request scoped to BGC.

**POS endpoints**
- `GET /api/pos/context`: Cashier → `{ branchId: theirs, canPickBranch: false }`; Owner
  single-branch → `canPickBranch: false`; Owner two active branches → `canPickBranch: true`,
  `branches` length 2.
- `GET /api/pos/registers`: availability states — own open session flagged `mine`, another
  user's flagged with their name, free register `openSession: null`; scoped user passing a
  foreign `branchId` → 403.

Update `IntegrationTest`: `CreateBranchAsync(name, code, ...)`; `AddTenantUserTokenAsync`
gains an optional `Guid? branchId` (defaults: null for Owner/Admin, required by callers for
scoped roles — helper asserts the invariant). `ResetDatabaseAsync` unaffected (tenant DBs
are dropped wholesale).

### 11.2 Unit — `tests/Negosio.UnitTests/Branches/`

- `BranchDomainTests`: `UpdateDetails` normalizes + `Touch`es + keeps code; `Deactivate` /
  `Reactivate` toggle + `Touch`; `AssignBranch` / `ClearBranch` on `User`.
- `BranchRolesTests`: `IsAllBranch` true only for Owner/Admin; `BranchScoped` set exact.
- `BranchAccessResolverTests` (with a fake `ICurrentUser` + in-memory context or a thin
  seam): all-branch → filter passthrough; scoped → forced branch; scoped + foreign target →
  `BRANCH_FORBIDDEN`; inactive target → `BRANCH_INACTIVE`.
- Role↔branch reconciliation rules (pure helper if extracted).

### 11.3 Frontend

`npm run lint` + `npm run build` clean at every commit. No frontend test runner.

---

## 12. Browser verification (real API + dev server, headless Chrome + CDP)

Two active branches `MAIN` + `BGC`. Do not claim a scenario passed unless executed.

**Branch CRUD** — Owner: add branch → appears; edit name → persists after reload; duplicate
code → clean validation; deactivate BGC → Inactive; reactivate → Active.

**Multi-branch (Owner)** — MAIN Widget A = 10, BGC Widget A = 4; Inventory branch selector
switches quantities with no leakage. Registers selector shows only the chosen branch's
registers. Sales branch filter shows the right sales; sale detail names its branch.

**POS (Owner, 2 branches)** — `/pos` → branch picker → register picker → opening cash →
terminal against the chosen branch; exit and re-enter → "Your open session — Continue".

**Per-branch numbering** — MAIN sale `00000001`, BGC sale `00000001`, MAIN return
`00000002`, BGC sale `00000002`. Inventory: MAIN sale drops MAIN stock only; MAIN return
restocks MAIN only.

**Cashier lock (the locked walkthrough)**
1. Owner creates MAIN + BGC. 2. Owner invites Cara (Cashier, BGC). 3. Cara accepts.
4. Cara logs in. 5. Cara sees only BGC context (no branch selectors anywhere).
6. `/pos` shows no branch picker. 7. Cara sees BGC registers only.
8. Cara picks an available register → 9. enters opening cash → 10. session opens under Cara.
11. A second BGC cashier sees that register "In use by Cara Cashier", disabled.
12. Cara clicks Exit (no close). 13. Cara returns to `/pos` → "Your open session — Continue".
14. Owner deactivates BGC. 15. Cara's next action / re-login → denied, `BRANCH_INACTIVE`
message. 16. Owner still logs in. 17. Owner sees Cara's open BGC session. 18. Owner
force-closes it with a counted amount → reconciliation shown, ClosedBy = Owner. 19. Owner
reactivates BGC. 20. Cara logs in again. 21. Owner reassigns Cara to MAIN. 22. Cara now
sees/operates MAIN only. 23. Manual `branchId = BGC` via devtools request → 403.

**Foreign-branch (Cashier)** — manual navigation / fetch to another branch's
inventory / register / POS / sales → clean rejection, never raw JSON.

**RBAC regression smoke** — `/dashboard /categories /products /inventory
/inventory/movements /registers /pos /sales /settings /staff /branches` for Owner and for
Cashier — correct render, no console errors, no raw error envelopes.

Zero console errors throughout.

---

## 13. Middleware / pipeline summary

```
ExceptionHandlingMiddleware
UseAuthentication            → JWT validated; OnTokenValidated: platform account active + role match (Phase 4, unchanged)
TenantResolutionMiddleware   → routes the tenant DB connection
BranchAccessMiddleware       → NEW: branch-scoped user's assigned branch must be active (else 401 BRANCH_INACTIVE)
UseAuthorization
MapControllers               → BranchAccessResolver enforces branch targeting inside services
```

---

## 14. Git

`feature/branch-management` off `master`. Staged commits (rename freely if the code suggests
better seams):

1. `feat(branches): branch domain methods + management service + policy + error codes`
2. `feat(branches): branch CRUD API + validators` (+ `GET` §6.1 behaviour)
3. `test(branches): branch management lifecycle + tenant isolation`
4. `feat(branches): /branches page, form modal, capability-gated nav`
5. `feat(staff): User.BranchId + invitation branch + staff-branch UI` (migration; Phase 4 svc + page changes)
6. `feat(branches): BranchAccessResolver + branch-scoped enforcement everywhere` (+ tests)
7. `feat(auth): branch-scoped users require an active branch to authenticate` (login + `BranchAccessMiddleware` + tests)
8. `feat(pos): one-session-per-user, session ownership, owner/admin force-close` (migration + index + tests)
9. `feat(pos): explicit branch + register-availability picker; context endpoint`
10. `feat(sales): branch filter; scoped dashboard metrics; active-only branch count`
11. `test(branches): browser-verification fixtures / docs` + final report in `handover.md`

**Do NOT merge.** Stop after verification; write the 9-section final report.
