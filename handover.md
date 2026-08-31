# Handover Summary

> Session date: 2026-08-31 (later session) · Supersedes the previous "merged Catalog + Inventory" handover.
> **Active branch: `feature/pos-frontend`** — the full POS frontend vertical, built and
> browser-verified this session. **19 commits ahead of `master`. NOT merged — waiting for your review.**
> `master` itself is unchanged from last session and has been pushed to `origin`.

---

## Project Context

- **Project:** **Negosio** — a multi-tenant SaaS platform for retail and food & beverage
  businesses. Owner signs up → picks a business type → describes a first branch → creates an
  owner account → lands in a tenant-scoped dashboard. Commercial surface: product catalog,
  inventory, and a retail POS (registers, sessions, sales, payments, returns).
- **Tech stack:**
  - **Backend:** C# / .NET 9, ASP.NET Core Web API, EF Core 9, SQL Server (LocalDB dev,
    Testcontainers/LocalDB tests). JWT bearer auth, ASP.NET Core `PasswordHasher` (PBKDF2),
    FluentValidation, Serilog. **Modular monolith**, Clean-Architecture-influenced:
    `Api → Infrastructure → Application → Domain`. Database-per-tenant.
  - **Frontend:** React 19, TypeScript (strict), Vite 8, React Router 7, TanStack Query v5,
    Tailwind v4 (custom tokens, light "professional cloud SaaS" style), lucide-react.
    Lint = `oxlint`. Path: `web/negosio-web/`. **No frontend test runner** (deliberate;
    verified via `npm run lint` + `npm run build` + headless-Chrome/CDP walkthroughs).
  - **Tests:** xUnit, FluentAssertions, `WebApplicationFactory`. 50 unit + 99 integration, all green.
- **Roadmap:** Phase 1 (SaaS foundation) ✅ · Phase 2 (Catalog + Inventory) ✅ · Phase 2.5
  (Database-per-Tenant) ✅ · **Phase 3 (Retail POS) — backend ✅ done last session, frontend
  ✅ done THIS session (on `feature/pos-frontend`, unmerged)** · Phase 4+ (F&B, purchasing,
  Azure) future.

---

## What this session did

Built the **entire POS frontend vertical** on `feature/pos-frontend`, following a
brainstorm → design spec → 13-task implementation plan → inline execution with per-task
`lint`+`build` gates → full end-to-end browser verification.

- **Design spec:** `docs/superpowers/specs/2026-08-31-pos-frontend-design.md`
- **Implementation plan:** `docs/superpowers/plans/2026-08-31-pos-frontend.md`
- **No backend changes** — every screen uses an already-built, tested Phase 3 endpoint.

### Four user-mandated refinements — all implemented and verified

1. **Checkout idempotency survives refresh/crash.** The unresolved `clientRequestId` is
   persisted in `localStorage` (key `negosio.pos.attempt.v1.<tenant>/<branch>/<register>/<session>`),
   restored on mount, kept across `NETWORK_ERROR` + transient concurrency retries, cleared only
   on confirmed success, rotated only after a definitive rejection + a meaningful cart edit.
   No payment details are ever persisted. **Verified in-browser:** reload mid-checkout → same
   id, cart restored, "Resuming an unfinished sale" banner.
2. **Per-register cart keys.** `TerminalCtx` carries `registerId`; cart + attempt keys are
   scoped to `tenant/branch/register/session`; pruning only ever removes stale keys for the
   **same** register. **Verified:** storage keys show the 4-part scope.
3. **Register resolution is derived state** (no `useEffect`-seeded state, no exhaustive-deps
   suppression) — `override ?? persisted-if-valid ?? sole-active-register ?? picker`.
4. **`saleMath.ts` is preview-only** — mirrors `SaleLineCalculator.cs`, never authoritative;
   the server's `SaleResultDto` / `SaleDetailDto` / `ReceiptDto` win on any mismatch.

### Files added (all under `web/negosio-web/src/`)

- **API/lib:** `api/pos.ts` (`registersApi`/`sessionsApi`/`posCatalogApi`/`checkoutApi`/`salesApi`/`taxSettingsApi`),
  POS section appended to `api/types.ts`, `lib/pos.ts` (labels + `suggestCashButtons`),
  `lib/saleMath.ts`, `lib/posStorage.ts`, `lib/returns.ts`, capabilities added to `lib/useCan.ts`
  (`register:manage`/`pos:operate`/`sales:view`/`refund:manage`), `lib/nav.ts` "Point of sale" group,
  routes in `App.tsx`.
- **Hooks:** `hooks/usePosCart.ts` (reducer + `localStorage`, lazy-seeded), `hooks/useBarcodeScanner.ts`
  (keyboard-wedge burst detector), `hooks/useTaxSettings.ts`.
- **Pages:** `pages/RegistersPage.tsx`, `pages/PosPage.tsx` (full-screen state machine),
  `pages/PosCompletePage.tsx`, `pages/SalesPage.tsx`, `pages/SaleDetailPage.tsx`, `pages/ReceiptPage.tsx`.
- **Components:** `components/registers/` (`RegisterFormModal`, `RegisterSessionCell`),
  `components/pos/` (`OpenSessionModal`, `CloseSessionModal`, `PosShell`, `RegisterPicker`,
  `PosSessionGate`, `PosTerminal`, `PosSearchBar`, `PosProductGrid`, `CartPanel`,
  `LineDiscountPopover`, `PaymentModal`),
  `components/sales/` (`StatusBadge`, `SaleItemsTable`, `SaleReturnsList`, `ReturnModal`).

### Routes

| Route | Shell |
|---|---|
| `/registers`, `/sales`, `/sales/:id`, `/sales/:id/receipt` | in-dashboard (`DashboardLayout`) |
| `/pos`, `/pos/complete/:saleId` | dedicated full-screen (no sidebar) |

---

## Verification performed this session

- **Backend:** `dotnet test Negosio.sln` → **50 unit + 99 integration, 0 failed** (~6m11s;
  regression guard — no backend files changed). Ran against `(localdb)\MSSQLLocalDB`.
- **Frontend:** `npm run lint` (oxlint) + `npm run build` (`tsc -b && vite build`) → **clean**
  at every one of the 13 tasks. Final bundle ≈ 440 kB js / 125 kB gz.
- **Backend contract drive (curl):** register create → session open/current/close (cash
  reconciliation correct) → pos catalog (`isAvailable:false` for 0-stock) → checkout
  (server-computed subtotal/discount/tax/total/change) → **idempotent replay**
  (`wasExistingRequest:true`, same sale) → `INSUFFICIENT_INVENTORY` 409 → receipt → sales list
  → sale detail → partial return (`RET-MAIN-000001`, status → `PartiallyRefunded`,
  `returnedQuantity` updated) → over-return `RETURN_QUANTITY_EXCEEDED` 400 → inventory reflects
  restock. All correct.
- **Browser (headless Chrome + CDP, real API + `npm run dev`):**
  - login → `/registers` (new "Point of sale" nav group; register row with live session state)
  - `/pos` full-screen: session gate → open with opening cash → terminal (62/38 split, product
    grid with stock chips, Widget B "Out of stock" disabled)
  - add Widget A ×2 → cart ₱24.00 → **storage keys have the 4-part scope** → Charge →
    PaymentModal (Amount due, method segments, quick-cash buttons, live Change) → Confirm →
    `/pos/complete/:id` (INV-MAIN-000002, Total/Paid/Change) → **cart + attempt keys cleared**
  - Print receipt → 80 mm monospace receipt renders, `?print=1` fires `window.print()`
  - `/sales` list → `/sales/:id` detail (items, payments, totals, Returns section)
  - **Start return** modal (stacked item rows, reason, refund method, estimated refund)
  - **Refresh mid-checkout** → cart restored, same `clientRequestId`, "Resuming an unfinished
    sale" banner
  - **Zero console errors** across every screen.
  - Screenshots: this session's scratchpad (`10-…` … `22-…`).

### Not exercised in-browser (noted, low risk)

- **Network-failure retry + INSUFFICIENT_INVENTORY recovery in the UI** — the code paths are
  built (see `PosTerminal` `onError`), and both errors were confirmed at the API level, but the
  offline-then-retry click sequence wasn't scripted. Worth a manual check.
- **Multi-register / multi-branch** — every test tenant has one branch and (now) one register;
  `RegisterPicker` and the branch columns only render when `> 1`, coded but unexercised.
- **Split tender / order-level discount** — deliberately out of scope (single tender, line
  discounts only).

---

## Current State

- **`feature/pos-frontend`:** 19 commits ahead of `master`. Working tree clean.
  `dotnet build` 0/0 · `dotnet test` green · `npm run lint` + `npm run build` clean.
- **`master`:** unchanged this session; already pushed to `origin` last session.
- **Dev data:** the `Negosio.InvUITest_MAIN_204CD2A1` tenant DB now has a register
  ("Front Counter" / FC1), a couple of closed sessions, sales `INV-MAIN-000001`
  (partially refunded) and `INV-MAIN-000002`, return `RET-MAIN-000001`, and Widget A stock
  drawn down by the test flow — all created this session for verification. Harmless.
- **Working dev login:** `invui@example.com` / `SecurePassword123!` (tenant "Inv UI Test",
  Owner role, one branch "Main Branch" / MAIN, products Widget A + Widget B).

### Still not built (the remaining frontend gap)

- **Dashboard UI is still Phase-1 only** — ignores `todaysSales` / `todaysTransactions` /
  `averageTransactionValue` / `lowStockItems` from `/api/dashboard`.
- **No `/settings` tax page** (`GET/PUT /api/settings/tax` exists; the POS reads it read-only).
- **No staff/user management** (API or UI) — every non-Owner RBAC role is inert in practice.
- **No add-branch / branch management** (API or UI).
- No F&B, reports, offline sync.

---

## Important Decisions (this session)

- **POS shell:** dedicated full-screen route outside `DashboardLayout`. Product area ~62%,
  cart ~38%.
- **Single tender** per sale (payments array length 1); cash shows tendered + change + quick-cash.
- **Line-level discounts only** (matches `CheckoutItemInput.Discount`); no order-level discount.
- **No "quick sell without a session"** — backend requires an open session; UI enforces it.
- **Session resolution by `registerId`**, not "one session per branch".
- `useCan` capability→role sets mirror `AuthorizationPolicies.cs`. Documented in `useCan.ts`.
- **No "Co-Authored-By: Claude" trailer** on any commit (repo convention).
- Return modal uses a **stacked item layout** (not a wide table) so it fits `Modal size="md"`.
- `usePosCart` is **lazy-seeded** from `localStorage` in the reducer initializer (the terminal
  only mounts once its context is fully resolved, so the seed key is stable); it never persists
  an empty cart.

---

## Next Steps

**Confirm direction with the user. Do not merge to `master` without their say-so.**

1. **Review `feature/pos-frontend`** — run `superpowers:requesting-code-review` or a manual
   pass; then `superpowers:finishing-a-development-branch` to merge + push when approved.
2. **Manual browser check of the two unscripted paths:** offline-then-retry at checkout
   (expect "couldn't confirm the sale" + safe Retry), and `INSUFFICIENT_INVENTORY` recovery
   (set a variant's stock low, oversell, expect the cart callout + stock refresh + a fresh id
   on the next edit).
3. **Small independent catch-up** (each ~1 slice): wire the **dashboard** metrics; a
   **`/settings` tax page**.
4. **Multi-branch / multi-register browser verification** once a way to create a second
   branch/register exists to exercise those code paths.

---

## Prompt for Next Claude Session

```
You are continuing work on Negosio — a multi-tenant .NET 9 / EF Core / SQL Server + React 19
SaaS platform (modular monolith) for retail & F&B. Read, in order:
  1. handover.md (repo root) — this file.
  2. docs/superpowers/specs/2026-08-31-pos-frontend-design.md — the POS frontend design.
  3. docs/superpowers/plans/2026-08-31-pos-frontend.md — the 13-task plan that was executed.

STATE (2026-08-31, later session):
  - `feature/pos-frontend` has the COMPLETE POS FRONTEND: /registers, /pos (full-screen:
    register picker → session gate → terminal with search/grid/cart/line-discounts/barcode →
    single-tender payment modal → persisted-clientRequestId checkout → /pos/complete → receipt),
    /sales, /sales/:id, returns modal. 19 commits ahead of master, NOT merged, awaiting review.
  - Verified: dotnet test 50u+99i green · npm lint+build clean · full browser walkthrough,
    zero console errors · idempotency-across-refresh confirmed in-browser · all backend
    contracts drive-tested via curl.
  - `master` unchanged this session, already on origin.
  - Dashboard UI still Phase-1. No /settings page. No staff mgmt. No branch mgmt.

WORKING DEV LOGIN: invui@example.com / SecurePassword123! (tenant "Inv UI Test", Owner,
  one branch, Widget A + Widget B, plus a register + sales created during verification).

DO NOW:
  1. Ask the user whether to (a) review + merge feature/pos-frontend, or (b) build something
     else (dashboard metrics / tax settings page are the small independent options).
  2. If reviewing: superpowers:requesting-code-review, then superpowers:finishing-a-development-branch.
  3. For any new UI feature: brainstorm first (superpowers:brainstorming), work on a feature
     branch, reuse the shared components (Modal/Table/Pagination/SearchInput/usePagedQuery/useCan).

GUARDRAILS — do not undo:
  - NO "Co-Authored-By: Claude" trailer on commits. Commit on feature branches, merge only on the user's word.
  - Never compute authoritative money/stock client-side; saleMath.ts is preview-only.
  - Never persist payment details; the POS clientRequestId lives in localStorage keyed
    tenant/branch/register/session and is cleared only on confirmed checkout success.
  - Tenant DB naming = Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8> (SqlDatabaseProvisioner).
  - AuthService uses ITenantDbContextFactory, never a scoped ITenantDbContext.
  - Run the API: ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api
    --launch-profile http   (plain `dotnet run` fails on a missing JWT signing key).
  - Kill any stale Negosio.Api / vite process before building or `dotnet test`.

If something is uncertain, verify against the repo — it is the source of truth.
```

### Useful commands

```bash
# build + test
dotnet build Negosio.sln
dotnet test Negosio.sln                 # ~6 min integration; safe on shared LocalDB

# run API (dev)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api --launch-profile http
#   http://localhost:5170 , swagger at /swagger

# frontend
cd web/negosio-web && npm run lint && npm run build
cd web/negosio-web && npm run dev       # http://localhost:5173

# branch
git checkout feature/pos-frontend       # the POS frontend work
git log --oneline master..HEAD          # the 19 commits under review
```
