# Handover Summary

> Session date: 2026-08-31 · Supersedes the previous Phase 2.5 handover.
> Branch: `master`. Working tree: only `handover.md` modified (this file).
> **All session work is now merged into `master`** (Catalog frontend, Inventory frontend +
> `GET /api/branches`, test-DB-isolation fix). Feature branches deleted. `master` is ahead
> of `origin/master` and has **not been pushed**.

---

## Project Context

- **Project:** **Negosio** — a multi-tenant SaaS platform for retail and food & beverage
  businesses. Owner signs up → picks a business type → describes a first branch → creates
  an owner account → lands in a tenant-scoped dashboard. Commercial surface today: product
  catalog, inventory, and a retail POS (sales / payments / returns).
- **Tech stack:**
  - **Backend:** C# / .NET 9, ASP.NET Core Web API, EF Core 9, SQL Server (LocalDB dev,
    Testcontainers/LocalDB tests). JWT bearer auth, ASP.NET Core `PasswordHasher` (PBKDF2),
    FluentValidation, Serilog. **Modular monolith**, Clean-Architecture-influenced:
    `Api → Infrastructure → Application → Domain`.
  - **Frontend:** React 19, TypeScript (strict), Vite 8, React Router 7, TanStack Query v5,
    Tailwind v4 (custom tokens, light "professional cloud SaaS" style), lucide-react.
    Lint = `oxlint`. Path: `web/negosio-web/`. **No frontend test runner** (deliberate;
    verified via `npm run lint` + `npm run build` + headless-Chrome/CDP walkthroughs).
  - **Tests:** xUnit, FluentAssertions, `WebApplicationFactory`. ~50 unit + ~95–99
    integration, all green.
- **Overall roadmap:** Phase 1 (SaaS foundation) ✅ · Phase 2 (Catalog + Inventory) ✅
  backend, frontend now done · Phase 2.5 (Database-per-Tenant) ✅ · Phase 3 (Retail POS)
  ✅ **backend only** — POS frontend not started · Phase 4+ (F&B, purchasing, Azure) future.
- **Main goal of the recent work:** **catch the frontend up to the backend.** The backend
  through Phase 3 is 100% built and tested; the browser UI was ~2 phases behind. This
  session merged the Catalog UI and built the Inventory UI.

---

## Current Task

This session did several connected pieces of work — **all now merged into `master`**:

1. **Phase 2.5 review + commit.** Reviewed the (previously uncommitted) database-per-tenant
   refactor, added a tenant-DB **naming change** (from `Negosio_Tenant_<guid>` to
   `Negosio.<SanitizedBusinessName>_<SanitizedBranchCode>_<TenantIdPrefix8>`), then
   committed everything as one Phase 2.5 commit on `master` (`601136f`).
2. **Catalog frontend** (Categories + Products + Variants UI). Full flow: brainstorm →
   spec → 16-task implementation plan → subagent-driven execution with per-task review →
   final whole-branch review → fix wave → **merged to `master`** (merge `349dea8`).
3. **Inventory frontend + `GET /api/branches`.** `/inventory` (stock levels) +
   `/inventory/movements` + adjust / opening-stock modals + optimistic-concurrency 409
   handling, plus the read-only branches endpoint. Built on `feature/inventory-frontend`,
   browser-verified end to end, **merged to `master`** (merge `0863368`).
4. **Test-DB-isolation bug fix.** The integration test harness was destroying developer
   tenant databases on a normal `dotnet test` run. Fixed on `fix/test-db-isolation`,
   **merged to `master`** (merge `f6f1feb`).
5. **Repo audit** + a **status briefing** for an external design advisor
   (`C:\Users\Ace\Desktop\negosio-status.md`).

All three feature branches were deleted after merge. `master` is ahead of `origin/master`
and has **not been pushed**.

- **User requirements / design references given for the Inventory slice:** reuse the
  Catalog design system + shared components; add `/inventory` and `/inventory/movements`;
  enable the Inventory sidebar item; use only the existing stable APIs
  (`GET /api/inventory`, `POST /api/inventory/adjustments`, `GET /api/inventory/movements`)
  plus the new `GET /api/branches`; support search/filter/pagination; show quantity on hand
  + reorder level; **subtle** low-stock / out-of-stock badges; adjustment modal supports
  increase/decrease and requires a reason; send `concurrencyToken` when adjusting an
  existing row; on `409 INVENTORY_CONCURRENCY_CONFLICT` **do not silently retry** — show a
  clear message, refetch, ask the user to retry with fresh data; opening stock needs no
  token and must work even with no existing rows and no POS registers; never compute
  authoritative stock client-side; never expose cost data to disallowed roles; respect RBAC
  via `useCan`; **no POS / Sales / Tax Settings / F&B work in this branch.**

---

## Completed Work

### Phase 2.5 commit (`master`, commit `601136f` "Phase 2.5: Database-per-Tenant architecture")

- Tenant DB naming is now `Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>` — see
  `src/Negosio.Infrastructure/Tenancy/SqlDatabaseProvisioner.cs`
  (`DatabaseNameFor(tenantId, businessName, initialBranchCode)`, regex
  `^Negosio\.[A-Za-z0-9]{1,40}_[A-Za-z0-9]{1,16}_[A-F0-9]{8}$`).
  `TenantServerRegistry.BuildConnectionString` uses `SqlConnectionStringBuilder` so the dot
  in the DB name is quoted correctly. Called from `TenantProvisioningService` with
  `command.BusinessName` + `command.Branch.Code`.
- Old shared `AppDbContext` / `IApplicationDbContext` fully removed. `PlatformDbContext`
  (3 tables) + `TenantDbContext` (17 tables), separate migration histories, one
  `PlatformBaseline` + one `TenantBaseline` migration (squashed).
- ADRs `docs/adr/0008`–`0012`; README "Phase 2.5" section.

### Catalog frontend (merged to `master`, ~17 commits, merge `349dea8`)

Spec: `docs/superpowers/specs/2026-08-30-catalog-frontend-design.md`.
Plan: `docs/superpowers/plans/2026-08-30-catalog-frontend.md`.
SDD ledger: `.superpowers/sdd/2026-08-30-catalog-frontend/progress.md`.

- **Foundation:** `src/api/catalog.ts` (categories/products/variants API modules),
  `src/api/query-string.ts` (`qs()`), catalog types + `PagedResult<T>` in `src/api/types.ts`;
  `src/hooks/usePagedQuery.ts` + `useDebouncedValue.ts` (list state ↔ URL, browser-Back
  resync); `src/lib/useCan.ts`, `src/lib/format.ts` (`formatMoney`/`formatRange`/
  `formatMarginPct`), `src/lib/formErrors.ts`, `src/lib/catalogRequests.ts`
  (`buildUpdateProductRequest` — the ONE place `UpdateProductRequest` is built; forces
  sku/barcode/cost/selling inert for variant products, which `ProductService.UpdateAsync`
  provably ignores).
- **UI primitives:** `Modal`, `ConfirmDialog`, `Table` (+ sortable `HeaderCell`),
  `Pagination`, `SearchInput` in `src/components/ui/`.
- **Pages:** `CategoriesPage` (modal CRUD + deactivate/reactivate), `ProductsPage`
  (search/filter/sort/paginate, cost-column role gating, name-is-only-link),
  `ProductDetailPage` (view + deactivate/reactivate + variant table + `VariantFormModal`),
  `ProductCreatePage`/`ProductCreateForm` (single-item | has-variants), `ProductEditPage`/
  `ProductEditForm`.
- **Nav:** `src/lib/nav.ts` → grouped `NAV_GROUPS`; Catalog group enabled; sidebar footer
  neutralised to "Negosio" (no phase text).
- Final Opus review fixed: first-variant-promote wiping a simple product's SKU/prices
  (`VariantFormModal` now takes `promoteFrom`), `usePagedQuery` browser-Back desync, Modal
  focus-trap escape, submit `preventDefault` ordering, silent form-error dead-ends.

### Inventory frontend + branches endpoint (merged to `master`, merge `0863368`)

**Commit `1b568d9` "feat: GET /api/branches":**
- `src/Negosio.Application/Branches/BranchContracts.cs` — `BranchDto(Id,Name,Code,IsActive)`,
  `IBranchQueryService`.
- `src/Negosio.Application/Branches/BranchQueryService.cs` — reads
  `ITenantDbContext.Branches` for `_currentUser.TenantId`, active only, ordered by name.
- `src/Negosio.Api/Controllers/BranchesController.cs` — `[Authorize]` `GET /api/branches`.
- `src/Negosio.Application/DependencyInjection.cs` — +1 line.
- `tests/Negosio.IntegrationTests/Catalog/BranchTests.cs` — 3 tests (lists the tenant's
  branch; a tenant never sees another tenant's branches; requires auth).

**Commit `2f2e533` "web: inventory frontend":**
- `src/api/inventory.ts` — `inventoryApi.{list,get,adjust,movements}` + `branchesApi.list`.
- `src/api/types.ts` — `BranchDto`, `InventoryRowDto`, `InventoryStatus` (string union
  `'OutOfStock'|'LowStock'|'InStock'`), `StockMovementDto`, `StockMovementType` (string
  union), `AdjustInventoryRequest`, `InventoryListParams`, `MovementListParams`.
- `src/lib/useCan.ts` — new `'inventory:write'` capability
  (Owner/Admin/Manager/InventoryStaff, mirrors `CatalogAccess.InventoryWriterRoles`).
- `src/lib/format.ts` — `formatQty` (≤3 dp, trailing zeros trimmed),
  `formatSignedQty` (`+5` / `−3`).
- `src/lib/movements.ts` — `MOVEMENT_TYPE_LABELS`, `STOCK_INCREASE_TYPES`,
  `movementTypeLabel` (kept out of the component file to satisfy
  `react/only-export-components`).
- `src/lib/nav.ts` — new "Inventory" nav group: "Stock levels" (`/inventory`) +
  "Stock movements" (`/inventory/movements`).
- `src/App.tsx` — `+/inventory`, `+/inventory/movements` protected routes.
- `src/pages/InventoryPage.tsx` — stock-levels list: `SearchInput` + branch (multi only)
  + category + stock-level filters; columns Product (link) / SKU / Branch (multi only) /
  On hand / Reorder level / Status / Actions (Adjust + History link); `StockStatusBadge`
  is subtle (plain "In stock" text, `warning` "Low stock", `danger` "Out of stock");
  header has "Movement history" link + "Add opening stock" button (gated).
- `src/pages/MovementsPage.tsx` — history: branch/product/type/date-range filters,
  columns When / Product / Type (`MovementTypeBadge`) / Change (signed, coloured) /
  Balance (`before → after`) / Reason / By / Branch (multi only). Newest first
  (backend order). Deep link `?productId=` from the list's per-row History link.
- `src/components/inventory/AdjustStockModal.tsx` — adjust an existing row. Context header
  (Product / Branch / **Current on hand**), Add/Remove direction toggle, Quantity, required
  Reason textarea, Reorder level (prefilled). Sends the row's `concurrencyToken`.
  **On `409 INVENTORY_CONCURRENCY_CONFLICT`:** no retry — shows a `Callout`, invalidates
  `['inventory']`, calls `inventoryApi.get(rowId)` to refresh the modal's current-on-hand +
  hidden token, **preserves the user's quantity + reason**, re-enables Submit.
  `400 INSUFFICIENT_INVENTORY` → inline quantity error; other → toast.
- `src/components/inventory/OpeningStockModal.tsx` — branch `Select` (auto-picked when 1) +
  product `Select` (active, tracked) + variant `Select` (when `hasVariants`) + opening qty +
  reorder level + reason. Sends `expectedConcurrencyToken: null`. If the product already has
  stock at that branch → backend `400 INVALID_INVENTORY_ADJUSTMENT` → "use Adjust from the
  list instead."
- `src/components/inventory/StockStatusBadge.tsx`, `MovementTypeBadge.tsx`.

### Test-DB-isolation fix (merged to `master`, merge `f6f1feb`, commit `69c9b6f`)

- `tests/Negosio.IntegrationTests/Infrastructure/NegosioApiFactory.cs` — removed
  `DropTenantDatabasesAsync` (blind `sys.databases LIKE 'Negosio.%'` + `DROP DATABASE`);
  added `DropProvisionedTenantDatabasesAsync()` which reads tenant DB names from **this
  run's own** `Negosio_Test_Platform_<guid>.TenantDatabases` and drops only those
  (guarded `IF DB_ID(...) IS NOT NULL` + `StartsWith("Negosio.")`). LocalDB fallback now
  writes a warning to `Console.Error`.
- `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs` — `ResetDatabaseAsync`
  now calls `Factory.DropProvisionedTenantDatabasesAsync()`; removed the unused
  `Microsoft.Data.SqlClient` import.
- `tests/Negosio.IntegrationTests/Platform/TestDatabaseIsolationTests.cs` — new test:
  a bystander `Negosio.NotOurs_DEV_00000000` database survives cleanup while the run's own
  provisioned tenant DB is dropped.

### Dev-database cleanup (LocalDB, not code)

- Dropped the empty shells `Negosio.CatalogUITest_MAIN_CF53D1D1` and
  `Negosio.AceHardware_01_C1EF4907` + their `Negosio_Platform` rows (they were the
  casualties of the test-harness bug — see Known Issues). Dev LocalDB now holds only
  `Negosio_Platform` and `Negosio.InvUITest_MAIN_204CD2A1`.

---

## Current State

**Working / verified on `master` (post-merge):**

- `dotnet build Negosio.sln` → **0 warnings / 0 errors.**
- `dotnet test Negosio.sln` → **50 unit + 99 integration, 0 failed** (base 95 + 3
  `BranchTests` + 1 `TestDatabaseIsolationTests`). ~6 min. Ran against the real
  `(localdb)\MSSQLLocalDB` after the isolation fix — the dev tenant DB
  `Negosio.InvUITest_MAIN_204CD2A1` survived (proving the fix).
- `npm run lint` (oxlint) + `npm run build` (`tsc -b && vite build`) → **clean.**
  Build ≈ 379 kB js / 111 kB gz.
- **Catalog UI** — browser-verified: categories CRUD, products list/filter/sort/paginate +
  refresh + browser-Back, product create/edit (simple + variant, variant prices provably
  unchanged on edit), first-variant promote pre-seeding, Modal a11y, 404.
- **Inventory UI** — browser-verified end to end against the real API on a fresh tenant
  with no stock: empty state → Add opening stock (branch auto-hidden for 1 branch) → list
  shows qty 50 → Adjust +5 → 55 → Movement history shows 2 rows with correct
  `before → after` balances → **concurrency conflict**: an out-of-band +7 then a UI submit →
  warning callout, current on-hand refreshed 55 → 62, quantity input preserved ("3"),
  no silent retry → resubmit with fresh token → 65, modal closes. All correct.
- **Working dev login:** `invui@example.com` / `SecurePassword123!` (tenant "Inv UI Test",
  has Widget A / Widget B + stock). Only dev DBs on LocalDB now: `Negosio_Platform` +
  `Negosio.InvUITest_MAIN_204CD2A1`.

**Not done (the frontend gap):**

- `master` has **not been pushed** to `origin`.
- **Dashboard UI is still Phase-1 only** — it ignores the sales / today's-transactions /
  low-stock numbers the `/api/dashboard` endpoint already returns.
- **No POS / Registers / Sales / Returns / Settings frontend** at all.

**Looks wrong / needs refinement (Inventory slice, non-blocking — noted in the slice report):**

- **Multi-branch code paths are untested in a browser** — every test tenant has exactly one
  branch, and there is no add-branch API/UI. The branch `Select` + branch column only
  render when `branches.length > 1`; that path is coded but unexercised. *Needs
  verification.*
- Inventory list has **no sortable columns** (`GET /api/inventory` has no `sortBy`;
  backend orders by product then variant).
- `OpeningStockModal` product picker is a plain `<Select>` capped at 100 products
  (`OPENING_STOCK_FETCH_LIMIT`) — same stopgap as the catalog category picker.
- Movement-history date filters send whole-day bounds in the browser's timezone
  interpretation (`T00:00:00Z` / `T23:59:59Z`) — acceptable, no tenant-timezone concept
  exists.
- Movement "Balance" column is right-aligned with an arrow expression (`50 → 55`) — reads
  slightly oddly; cosmetic.

---

## Known Issues / Bugs

- **RESOLVED + MERGED THIS SESSION — the test harness destroyed developer data.** With
  Docker absent, `NegosioApiFactory` silently falls back to `(localdb)\MSSQLLocalDB` (the
  dev instance). Its cleanup ran `SELECT name FROM sys.databases WHERE name LIKE 'Negosio.%'`
  → `DROP DATABASE` for each match — which includes real dev tenant DBs. A normal
  `dotnet test Negosio.sln` run this session **dropped the "Catalog UI Test" and
  "Ace Hardware" tenant databases**; API startup then re-created them schema-only (no owner
  user), so their logins 401'd with "The authenticated user no longer exists." Fixed
  (merge `f6f1feb`) — cleanup now drops only DBs the run provisioned. The empty shells +
  platform rows were cleaned up manually. The lost tenant data (registrations + a little
  seeded catalog/stock) is **unrecoverable** — no real business data was in them.
- **Stale dev-server trap (environment, not code):** a Visual Studio / C# Dev Kit
  `dotnet run` process keeps `bin/*.dll` locked → `MSB3061 Unable to delete file` on
  build, and can serve an old schema. Kill any `Negosio.Api` / `dotnet run` process (by
  port 5170 or name) before building or verifying.
- **`dotnet run` without the launch profile fails** (`JwtOptions.SigningKey is required`)
  because `appsettings.Development.json` isn't loaded. Run
  `ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api --launch-profile http`.
- **`sqlcmd -i <file>`** fails with "Access is denied" on this machine — use
  `-Q "<query>"` inline.
- README intro still says "This repository contains **Phase 1**" — stale since Phase 2.
  *Low priority.*
- No screenshots committed; browser-walkthrough screenshots live only in this session's
  scratchpad.

---

## Important Decisions

- **Tenant DB naming** = `Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>`. Business name +
  branch code sanitised to `[A-Za-z0-9]` and truncated (40 / 16 chars; fallbacks `Tenant`
  / `MAIN`); the 8-hex tenant-id suffix guarantees uniqueness. Immutable after
  provisioning. Composed server-side only, regex-validated, bracket-quoted. **Verified**
  (not assumed) that `ProductService.UpdateAsync` ignores `sku/barcode/cost/selling` for
  variant products (they're only read inside `if (!product.HasVariants)`).
- **`GET /api/branches` chosen over deriving branch identity from inventory rows or POS
  registers** — the frontend genuinely needs a branch id for opening stock, and no other
  endpoint exposes one. Read-only, tenant-scoped, `[Authorize]` (any authenticated tenant
  user), active branches only, **no branch CRUD**. Opening stock must work on a tenant with
  no existing inventory rows and no registers — this endpoint makes that possible.
- **Test cleanup must only drop databases the run created**, never a blind
  `sys.databases` scan. LocalDB fallback stays (it's needed with no Docker) but is now
  safe. A hard opt-in gate on LocalDB was considered and rejected as too disruptive to the
  no-Docker dev workflow; the surgical cleanup is the real protection.
- **`useCan` capabilities:** `catalog:write` (Owner/Admin/Manager), `inventory:write`
  (+ InventoryStaff), `costs:view` (+ InventoryStaff). Mirrors
  `src/Negosio.Application/Catalog/CatalogAccess.cs`. **Invariant: `catalog:write` ⊆
  `costs:view`** — `catalogRequests.ts` relies on it (documented in `useCan.ts`).
- **`usePagedQuery`** owns list state and syncs it to the URL (`?page`/`?q`/`?sort`/`?dir`
  + one key per filter). Any search/filter/sort change resets page to 1. Debounced search
  350 ms. **Pass `defaultFilters` as a module-level `const`** — the hook memoizes on it by
  reference. It has a browser-Back resync effect.
- **`buildUpdateProductRequest` is the single place an `UpdateProductRequest` is built**
  (edit form + product reactivate/deactivate share it).
- **Inventory UI shows no cost data at all** in this slice — inventory management is about
  quantities, and it sidesteps cost redaction entirely.
- **Never compute authoritative stock client-side** — every quantity shown comes straight
  from the DTO; client checks ("can't remove more than on hand") are pre-submit hints only.
- **No "Co-Authored-By: Claude" / "Generated with Claude Code" line on any commit**
  (repo convention — every commit after the initial one omits it).
- Rejected earlier in the catalog work (do not reintroduce): injecting a scoped
  `ITenantDbContext` into `AuthService` (use `ITenantDbContextFactory`); a
  `Persistence.Tenant` migration namespace that shadows the `Tenant` domain class; EF
  global query filters on a shared DB.

---

## Files to Review

Start here to rebuild context, roughly in order of usefulness:

1. **`C:\Users\Ace\Desktop\negosio-status.md`** — the paste-ready design briefing written
   this session (phase status, architecture facts, what works / doesn't, open design
   questions). The best single overview.
2. `.superpowers/sdd/2026-08-30-catalog-frontend/progress.md` — the SDD ledger for the
   Catalog build: every task, ruling, and deferred minor.
3. `docs/superpowers/specs/2026-08-30-catalog-frontend-design.md` +
   `docs/superpowers/plans/2026-08-30-catalog-frontend.md` — Catalog design + plan
   (the pattern the Inventory slice follows).
4. **Inventory slice** (on `master`):
   `src/pages/InventoryPage.tsx`, `src/pages/MovementsPage.tsx`,
   `src/components/inventory/AdjustStockModal.tsx`,
   `src/components/inventory/OpeningStockModal.tsx`,
   `src/api/inventory.ts`, and the inventory section of `src/api/types.ts`.
5. **Branches endpoint** (on `master`):
   `src/Negosio.Application/Branches/*`, `src/Negosio.Api/Controllers/BranchesController.cs`,
   `tests/Negosio.IntegrationTests/Catalog/BranchTests.cs`.
6. **Test fix** (on `master`):
   `tests/Negosio.IntegrationTests/Infrastructure/NegosioApiFactory.cs`
   (`DropProvisionedTenantDatabasesAsync`),
   `tests/Negosio.IntegrationTests/Infrastructure/IntegrationTest.cs` (`ResetDatabaseAsync`),
   `tests/Negosio.IntegrationTests/Platform/TestDatabaseIsolationTests.cs`.
7. Backend inventory contract (unchanged, for the UI):
   `src/Negosio.Application/Inventory/InventoryContracts.cs`,
   `src/Negosio.Application/Inventory/InventoryService.cs`,
   `src/Negosio.Api/Controllers/InventoryController.cs`.
8. Shared frontend foundation: `src/hooks/usePagedQuery.ts`, `src/lib/useCan.ts`,
   `src/lib/format.ts`, `src/components/ui/` (Modal, Table, Pagination, SearchInput,
   ConfirmDialog), `src/lib/nav.ts`, `src/App.tsx`.
9. `src/Negosio.Api/Authorization/` — the 8 RBAC policies.
10. Memory files at
    `C:\Users\Ace\.claude\projects\c--Users-Ace-Documents-Negosio\memory\` — `MEMORY.md`
    index + `catalog-frontend.md`, `phase-2_5-database-per-tenant.md`,
    `visual-verify-headless-chrome.md`.

---

## Next Steps

Prioritized checklist. **Confirm the direction with the user; work on feature branches, not
directly on `master`.**

1. **(Optional) Push `master` to `origin`** — it's ahead and unpushed. Ask the user first.
2. **Small independent catch-up tasks** (each ~1 short slice, no dependency on POS):
   - Wire the **dashboard** to render `todaysSales` / `todaysTransactions` /
     `averageTransactionValue` / `lowStockItems` from `/api/dashboard` (types already
     corrected in `src/api/types.ts`; the DTO is `DashboardResponse`).
   - A **`/settings` tax page** for `GET/PUT /api/settings/tax` (contract in
     `src/Negosio.Application/Settings/TenantSettingsService.cs`).
3. **POS frontend** — the big remaining vertical, in dependency order:
   Registers/Sessions UI → POS checkout screen (cart, barcode/search, discounts, payment,
   change, receipt/print) → Sales history + detail → Returns. The POS backend is stable
   and idempotent; build against it as-is. **Do a brainstorming pass first** — the
   checkout-screen UX is the main open design question (see `negosio-status.md` §9).
4. **Multi-branch browser verification** of the Inventory UI once/if a way to create a
   second branch exists (currently impossible — no add-branch API/UI).

---

## Prompt for Next Claude Session

```
You are continuing work on Negosio — a multi-tenant .NET 9 / EF Core / SQL Server + React 19
SaaS platform (modular monolith) for retail & F&B businesses. Read these first, in order:
  1. C:\Users\Ace\Desktop\negosio-status.md — the design/architecture briefing (best overview).
  2. handover.md (repo root) — this file, full context for where we are.
  3. .superpowers/sdd/2026-08-30-catalog-frontend/progress.md — how the Catalog UI was built.

STATE (2026-08-31 — everything below is on `master`, all feature branches merged + deleted):
  - `master` has: Phase 1 + Phase 2 (Catalog + Inventory) BACKEND + Phase 2.5
    (database-per-tenant) + Phase 3 (Retail POS) BACKEND + the full CATALOG FRONTEND
    (categories, products, variants) + the full INVENTORY FRONTEND (/inventory stock levels,
    /inventory/movements, adjust + opening-stock modals with 409 concurrency handling) +
    `GET /api/branches` (read-only) + a test-harness fix so `dotnet test` no longer drops
    developer databases.
  - Verified: dotnet build 0/0 · dotnet test 50 unit + 99 integration green · npm lint +
    build clean. `master` is AHEAD of `origin/master` and has NOT been pushed.
  - The POS FRONTEND does not exist. Dashboard UI is still Phase-1 only (ignores the sales
    metrics the API returns). No /settings page. No branch-management or staff-management
    (API or UI); every non-Owner RBAC role is inert in practice.

WORKING DEV LOGIN: invui@example.com / SecurePassword123! (tenant "Inv UI Test", has stock).
Only dev DBs on (localdb)\MSSQLLocalDB: Negosio_Platform + Negosio.InvUITest_MAIN_204CD2A1.

DO NOW:
  1. Ask the user what to build next. Likely candidates, in order:
     (a) dashboard metrics + a /settings tax page — small, independent, no POS dependency;
     (b) the POS frontend vertical: Registers/Sessions UI → POS checkout screen (cart,
         barcode/search, discounts, payment, change, receipt/print) → Sales history +
         detail → Returns.
  2. For any UI feature, run a brainstorming pass first (superpowers:brainstorming). The POS
     checkout-screen UX is the main undecided design question — see negosio-status.md §9.
  3. Work on a feature branch. Reuse the shared frontend foundation, do not reinvent it.
  4. Optionally offer to push `master` to `origin` (it's unpushed) — ask first.

GUARDRAILS — do not undo these deliberate decisions:
  - NO "Co-Authored-By: Claude" / "Generated with Claude Code" line on any commit.
  - Commit on feature branches, not directly on master; merge only when the user says so.
  - Tenant DB naming is `Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>` (SqlDatabaseProvisioner).
  - AuthService uses ITenantDbContextFactory, never a scoped ITenantDbContext.
  - Frontend: reuse the shared components (Modal/Table/Pagination/SearchInput/usePagedQuery/
    useCan) — don't reinvent. `defaultFilters` for usePagedQuery must be a module-level const.
  - Never compute authoritative stock/prices client-side; never show cost data to roles
    outside costs:view (Owner/Admin/Manager/InventoryStaff) — always null-check the API field.
  - Backend integration tests: with no Docker they fall back to (localdb)\MSSQLLocalDB.
    This is now safe (cleanup drops only DBs the run provisioned). Before verifying, kill
    any stale `Negosio.Api` / `dotnet run` process (locks bin DLLs → MSB3061).
  - Run the API with: ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api
    --launch-profile http   (plain `dotnet run` fails on a missing JWT signing key).

If something is uncertain, verify against the repo — it is the source of truth. Do not
trust planning docs as proof of what's implemented.
```

### Useful commands

```bash
# build + test
dotnet build Negosio.sln
dotnet test Negosio.sln                 # ~6 min integration; safe on shared LocalDB (drops only what the run made)

# run API (dev) — needs the Development env + launch profile
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api --launch-profile http
#   http://localhost:5170 , swagger at /swagger

# frontend
cd web/negosio-web && npm run lint && npm run build
cd web/negosio-web && npm run dev       # http://localhost:5173

# inspect dev databases
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "SELECT name FROM sys.databases WHERE name LIKE 'Negosio%' ORDER BY name;"

# EF (always pass --context)
dotnet dotnet-ef migrations list --context PlatformDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api
dotnet dotnet-ef migrations list --context TenantDbContext   --project src/Negosio.Infrastructure --startup-project src/Negosio.Api
```
