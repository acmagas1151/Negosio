# Handover Summary

> Session date: 2026-09-01 · Supersedes the Phase 4 handover. Phase 5 is complete and merged.

## Project Context

- **Project:** Negosio — a multi-tenant SaaS platform for retail and food & beverage
  businesses, database-per-tenant. Owner registers → picks a business type → describes a
  first branch → creates an owner account → lands in a tenant-scoped dashboard. Commercial
  surface: product catalog, inventory, retail POS (registers, sessions, sales, payments,
  returns), staff & access management, and now branch management.
- **Tech stack:**
  - **Backend:** C# / .NET 9, ASP.NET Core Web API, EF Core 9, SQL Server (LocalDB dev),
    JWT bearer auth, ASP.NET Core `PasswordHasher` (PBKDF2), FluentValidation, Serilog.
    Modular monolith: `Api → Infrastructure → Application → Domain`.
  - **Frontend:** React 19, TypeScript (strict), Vite 8, React Router 7, TanStack Query v5,
    Tailwind v4, lucide-react. Lint = `oxlint`. Path: `web/negosio-web/`. No frontend test
    runner — verified via `npm run lint` + `npm run build` + headless-Chrome/CDP walkthroughs.
  - **Tests:** xUnit, FluentAssertions, `WebApplicationFactory`.
- **Main goal of this session:** implement **Phase 5 — Branch Management & Branch-Scoped
  Access**, i.e. make the pre-existing multi-branch architecture reachable through the API
  and UI, and lock every non-Owner/Admin staff member to exactly one branch.

## Current Task

Phase 5 is **done and merged**. There is no open task right now — the session ended with a
merge to `master` and an explicit instruction not to start Phase 6 automatically. The next
session's job is to **pick the next feature with the user**, not to continue Phase 5 work.

- **Feature implemented:** Branch Management v1 (CRUD, lifecycle) + a branch-scoped access
  model applied across the whole backend and the POS/Staff frontend.
- **User requirements (locked rules, verbatim intent):**
  - Role = *what* a user may do; branch assignment = *where*; branch active-status =
    *whether* a branch-scoped user may use Negosio at all.
  - All-branch roles: `Owner`, `Admin` (no `BranchId`). Branch-scoped: `Manager`, `Cashier`,
    `InventoryStaff`, `KitchenStaff`, `Viewer` (exactly one `BranchId`).
  - A branch-scoped user whose branch is deactivated is locked out **immediately** — at
    login and on the very next request with an already-issued JWT (no waiting for expiry).
  - A Cashier selects/opens a register **only from the POS flow**, never via `/registers`
    (management page — Owner/Admin/Manager only).
  - Register-session model: one open session per register **and** per user (DB-enforced);
    checkout/close require session ownership; **Owner/Admin-only** force-close (not Manager)
    with the same cash reconciliation.
  - Visible Sale/Return numbers are exactly **7 digits** (`0000001`), one shared
    atomic per-`(tenant, branch)` sequence — narrowed from 8 digits mid-phase.
  - `DO NOT MERGE` was in force for most of the session; the user explicitly approved the
    merge in the final message of this session.

## Completed Work

**Backend — new/changed (all now on `master`, merge commit `621aa8e`):**

- 4 EF Core migrations: `AddUserBranch` (tenant — `User.BranchId` + backfill),
  `AddStaffInvitationBranch` (platform), `PerBranchDocumentNumberUniqueness` (tenant —
  `Sale`/`SaleReturn` unique indexes moved to `(TenantId, BranchId, Number)`),
  `SessionOwnerOpenIndex` (tenant — one open session per user).
- `Branch` domain: `UpdateDetails`, `Deactivate`, `Reactivate` (code stays immutable).
- New `BranchManagementService`, `BranchAccessResolver`, `BranchValidators`,
  `PosContextService`.
- `BranchAccessResolver` wired into `DashboardService`, `InventoryService`,
  `RegisterService`, `PosCatalogService`, `CheckoutService`, `SaleQueryService`,
  `ReceiptService`, `ReturnService`.
- `AuthService.LoginAsync` — branch-active login gate. New
  `Negosio.Api/Middleware/BranchAccessMiddleware.cs` — per-request branch-active gate.
- `RegisterSessionService` — `CASHIER_SESSION_OPEN`, ownership checks, `ForceCloseAsync`.
  `RegisterService.ListAsync/GetAsync` now include each register's open-session state.
- `StaffService` — branch on invite, role↔branch reconciliation, new `ChangeBranchAsync`.
- `DocumentNumberService` — `D8` → `D7` format.
- New policies: `BranchManage`, `RegisterForceClose` (both Owner/Admin).
- New endpoints: full `/api/branches` CRUD, `GET /api/pos/context`,
  `GET /api/pos/registers`, `POST /api/register-sessions/{id}/force-close`,
  `POST /api/staff/{id}/branch`.
- New error codes: `LAST_ACTIVE_BRANCH`, `BRANCH_INACTIVE`, `BRANCH_FORBIDDEN`,
  `SESSION_NOT_OWNED`, `CASHIER_SESSION_OPEN`, `STAFF_HAS_OPEN_REGISTER_SESSION`.

**Frontend — new/changed:**

- `pages/BranchesPage.tsx`, `components/branches/BranchFormModal.tsx` /
  `BranchStatusBadge.tsx`, `api/branches.ts` — `/branches` management screen.
- `pages/PosPage.tsx` rewritten around `GET /api/pos/context`; new
  `components/pos/BranchPicker.tsx`; `components/pos/RegisterPicker.tsx` reworked for
  Available / "In use by \<name\>" / "Your open session — Continue"; `PosSessionGate.tsx`
  gained "← Back to registers" + "Exit POS".
- `components/registers/RegisterSessionCell.tsx` + `CloseSessionModal.tsx` — Owner/Admin
  force-close UI on `/registers`.
- Staff: `ChangeBranchModal.tsx` (new), `InviteStaffModal.tsx` / `ChangeRoleModal.tsx`
  gained a branch field, `StaffPage.tsx` gained a Branch column.
- `lib/nav.ts` / `App.tsx` — `/registers` route + nav gated on `register:manage` (was
  `pos:operate`) so a Cashier can no longer reach it; Reports placeholder and the Dashboard
  "Manage registers" quick action gated the same way.
- `pages/SalesPage.tsx` / `SaleDetailPage.tsx` — branch filter/column, conditional on
  `branches.length > 1`; transaction numbers render as `#0000001` / "Sale #0000001" /
  "Return 0000002 · for sale 0000001".

**Tests:** 88 unit / 162 integration, all green (was 76 / 114 before this phase). New test
files under `tests/Negosio.IntegrationTests/Branches/`:
`BranchManagementTests`, `BranchAuthTests`, `BranchScopedAccessTests`,
`RegisterSessionModelTests`, `StaffBranchTests`, `PosContextTests`; unit
`BranchDomainTests`, `BranchRolesTests`.

**Docs:** `docs/superpowers/specs/2026-09-01-branch-management-design.md`,
`docs/superpowers/plans/2026-09-01-branch-management.md`, `docs/phase-5-status.md` (the
full 9-section final report), `docs/adr/0007-sale-numbering-strategy.md` updated.

## Current State

- **Working:** everything listed above, verified on `master` post-merge:
  `dotnet build` 0/0, `dotnet test Negosio.sln` = **88 unit / 162 integration, 0 failed**,
  `npm run lint` clean, `npm run build` clean (474.67 kB js / 132.29 kB gz).
- **Merged & pushed:** `feature/branch-management` → `master` via `--no-ff` merge
  (`621aa8e`), pushed to `origin/master`. Working tree clean. The feature branch was **not**
  deleted.
- **Partially working / not fully polished:** register/session availability on the POS
  picker is a snapshot per fetch, not live-pushed (acceptable per the phase's scope; refetch
  on conflict covers it). No dedicated UI to see "N staff currently locked out" beyond the
  deactivate-confirmation staff count.
- **Dev data residue:** the "Inv UI Test" tenant DB now also contains disposable
  `Verify A / Verify B` branches and `ca.*@ex.com` / `mgr.*@ex.com` test accounts created by
  the verification scripts — harmless, not cleaned up. **Needs verification** whether the
  user wants these purged before further manual testing.

## Known Issues / Bugs

None outstanding that were left unresolved — every scenario the phase's spec asked for was
implemented and verified (either via the API/browser walkthrough or an integration test).
Two soft notes carried into the final report as deliberate, not bugs:

- Force-close is intentionally Owner/Admin only, not exposed to Manager — confirmed
  behavior, not a gap.
- The concurrent double-open register race and the Manager-hides-force-close-button UI path
  were verified via integration tests / API status codes rather than a live two-browser
  session — noted explicitly in the final report as "not run in a live browser session".

No screenshots are attached to this handover; the verification screenshots
(`p5-01`…`p5-10`, plus earlier `b*`/staff/POS screenshots) live only in this session's local
scratchpad directory (not committed to the repo) and are not guaranteed to exist for the
next session.

## Important Decisions

- **Branch code is immutable after creation** — chosen over editable because it seeds the
  (frozen, one-time) tenant-DB-name suffix and reserved future PO/transfer number prefixes;
  name/address remain editable.
- **Branch-scoped role set is fixed:** `Manager, Cashier, InventoryStaff, KitchenStaff,
  Viewer`. `Owner, Admin` are the only all-branch roles. This mirrors backend
  (`BranchRoles.cs`) and frontend (`lib/roles.ts`) — keep them in sync if ever revisited.
  Do not invent additional all-branch or branch-scoped roles.
- **Branch is enforced everywhere, not just POS** — inventory, movements, registers,
  catalog, checkout, sales, sale detail/receipt, returns, dashboard all go through the same
  `BranchAccessResolver`. Do not bypass it with ad-hoc branch checks in a new feature.
- **No operate-through-another's-session override** — only force-close (Owner/Admin) exists.
  An explicit "manager can operate a subordinate's session" feature was considered and
  rejected for this phase; do not add it without a fresh design discussion.
- **JWT does not carry branch** — branch is resolved fresh from the tenant DB on every
  request (`BranchAccessResolver`, `BranchAccessMiddleware`). This is why a branch
  reassignment or a branch deactivation take effect immediately without forcing re-login (a
  role change still needs re-login, since role *is* in the JWT — that's an older, separate
  decision from Phase 4, left unchanged).
- **Transaction number format was changed mid-phase** from 8 digits to 7 digits
  (`0000001`) by explicit user instruction after the numbering architecture already
  shipped. The architecture (atomic per-branch counter, shared sale/return sequence, never
  `COUNT(*)+1`) was **not** re-litigated — only the `D8`→`D7` format string and the
  now-per-branch unique index.
- **Do not weaken tests/lint/TypeScript/authorization to make things pass** — repeated
  instruction throughout the phase; every fix in this session was a real fix, never a
  weakened assertion.

## Files to Review

Start with these, in order:

1. `handover.md` (this file, repo root).
2. `docs/phase-5-status.md` — the full 9-section final report (architecture, backend,
   frontend, correctness matrix, security, tests, browser verification, git state,
   remaining limitations).
3. `docs/superpowers/specs/2026-09-01-branch-management-design.md` — the approved design.
4. `docs/superpowers/plans/2026-09-01-branch-management.md` — the 12-task implementation
   plan that was executed (plus its "Addendum — locked changes" section for the mid-phase
   D7/cashier-lockout changes).
5. `src/Negosio.Application/Branches/BranchAccessResolver.cs` — the enforcement core; any
   new branch-aware feature should call through this.
6. `src/Negosio.Application/Branches/BranchRoles.cs` and
   `web/negosio-web/src/lib/roles.ts` — the role↔branch-scope mapping (keep in sync).
7. `src/Negosio.Api/Middleware/BranchAccessMiddleware.cs` and
   `src/Negosio.Application/Auth/AuthService.cs` — the branch-active authentication gate.
8. `web/negosio-web/src/pages/PosPage.tsx` — the branch/register resolution flow, if
   touching POS again.

## Next Steps

1. **Ask the user what to build next** — do not start a new phase automatically. Phase 5's
   own final report suggested candidates: Void/refund-without-return, Reporting v1 (X/Z,
   sales by day/branch/cashier), Purchasing & supplier receiving (the
   `DocumentNumberCounter` table already reserves a `PurchaseOrder` type), stock transfers
   between branches, or the F&B vertical (tables, KDS, `KitchenStaff` becomes real).
2. If the user wants to keep testing Phase 5 manually first: decide whether to purge the
   disposable `Verify A/B` branches and `ca.*@ex.com`/`mgr.*@ex.com` test accounts from the
   dev tenant DB, or leave them (harmless either way).
3. For whatever comes next: if it's a new feature area, run it through
   `superpowers:brainstorming` before writing code (per this repo's established workflow —
   see the skill listing), work on a fresh feature branch off `master`, and do not merge
   without the user's explicit go-ahead (this session's merge was explicitly requested by
   the user in their final message — that approval does not carry forward to future work).
4. Before any `dotnet build`/`dotnet test`, kill stale `Negosio.Api`/`vite` processes first
   (repo convention — LocalDB file locks otherwise).

## Prompt for Next Claude Session

```
You are continuing work on Negosio — a multi-tenant .NET 9 / EF Core / SQL Server + React 19
SaaS platform (modular monolith, database-per-tenant) for retail & F&B. Read handover.md
(repo root) first, then docs/phase-5-status.md for full detail if needed.

STATE (2026-09-01):
  - Phase 5 (Branch Management & Branch-Scoped Access) is COMPLETE and MERGED to master
    (merge commit 621aa8e, pushed to origin/master). feature/branch-management still exists
    locally (not deleted) but master is now ahead-equivalent — work from master.
  - Verified on master: dotnet build 0/0, dotnet test Negosio.sln = 88 unit + 162
    integration (0 failed), npm run lint clean, npm run build clean.
  - Every non-Owner/Admin role is now bound to exactly one branch (User.BranchId); every
    branch-aware endpoint goes through BranchAccessResolver; branch-scoped users are locked
    out immediately (login + per-request) when their branch is deactivated; POS register
    sessions are ownership-enforced with Owner/Admin-only force-close; Sale/Return numbers
    are 7 digits (0000001), one shared per-branch sequence.

DO NOW:
  1. Ask the user what to build next — do NOT start a new phase automatically. Candidates
     from the Phase 5 report: Void, Reporting v1, Purchasing & receiving, stock transfers,
     F&B vertical.
  2. For any new feature: use superpowers:brainstorming first, work on a fresh feature
     branch off master, and do not merge without the user's explicit go-ahead in that
     conversation (do not assume a prior merge approval carries forward).

GUARDRAILS — do not undo:
  - Branch-scoped roles are fixed: Manager, Cashier, InventoryStaff, KitchenStaff, Viewer.
    All-branch: Owner, Admin only. Don't invent new categories without a design discussion.
  - Every branch-aware read/write must go through BranchAccessResolver
    (src/Negosio.Application/Branches/BranchAccessResolver.cs) — don't hand-roll new
    branch-filtering logic.
  - Branch code is immutable after creation; only name/address are editable.
  - JWT does not carry branch (resolved fresh per request) — role changes still require
    re-login (Phase 4 decision, unchanged); branch changes/deactivations do not.
  - Force-close is Owner/Admin only, never Manager, even though Manager has RegisterManage.
  - Sale/Return numbers are exactly 7 digits (0000001) — do not change the format again
    without an explicit new instruction.
  - No "Co-Authored-By: Claude" / "Generated with Claude Code" trailer on commits.
  - Run the API: ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api
    --launch-profile http
  - Kill any stale Negosio.Api / vite process before building or running dotnet test.

If something is uncertain, verify against the repo — it is the source of truth. Do not
assume dev-data (e.g. the "Verify A/B" branches or test cashier accounts left in the
Inv UI Test tenant) is meaningful; it is disposable verification residue unless the user
says otherwise.
```
