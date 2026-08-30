# Handover Summary

> Session date: 2026-08-29 · Branch: `master` · Nothing committed yet.

## Project Context

- **Project:** **Negosio** — a multi-tenant SaaS platform for retail and food & beverage
  businesses. A business owner signs up, picks a business type, describes their first branch,
  creates an owner account, and lands in a tenant-scoped dashboard.
- **Tech stack:**
  - Backend: C# / .NET 9, ASP.NET Core Web API, EF Core 9, SQL Server (LocalDB for dev,
    Testcontainers/LocalDB for tests), JWT bearer auth, ASP.NET Core `PasswordHasher` (PBKDF2),
    FluentValidation, Serilog. Modular monolith, Clean-Architecture-influenced
    (`Api → Infrastructure → Application → Domain`).
  - Frontend: React 19, TypeScript, Vite 8, React Router 7, TanStack Query, Tailwind v4,
    lucide-react, Inter font. Lint = `oxlint`. Located at `web/negosio-web/`.
  - Tests: xUnit, FluentAssertions, `WebApplicationFactory`.
- **Overall roadmap:** Phase 1 (SaaS foundation) ✅ · Phase 2 (Catalog + Inventory) ✅ ·
  **Phase 2.5 (Database-per-Tenant) ✅ this session** · Phase 3 (Retail POS backend) ✅ (frontend
  pending) · Phase 4+ (F&B, purchasing, Azure) future.

## Current Task

**Phase 2.5 — Database-per-Tenant Architecture Refactor.** A **persistence-only** refactor: move the
physical tenancy boundary from row-level isolation in one shared database (`WHERE TenantId = @x`
everywhere) to:

- **one operational SQL database per tenant** (`Negosio_Tenant_<tenantId:N>`), and
- a small **platform / control-plane database** (`Negosio_Platform`) holding tenant identity, DB
  routing, and the login directory.

Hard requirements from the user's spec:
- No business-logic rewrite, no microservices / Redis / Service Bus / sharding.
- **All Phase 1–3 behaviour and every API contract unchanged** → the React app needs no functional
  edits (regression check only).
- Present the architecture map before coding (done, approved), execute in spec order, then
  **pause for final architecture review** (we are at this pause now).

**User decisions locked in (via AskUserQuestion):**
1. Password hash authority = **Platform DB only** (`PlatformUserLogin.PasswordHash` is the sole
   authority; tenant `User` has no hash).
2. Email uniqueness = **keep globally unique** (`PlatformUserLogin.EmailNormalized` global unique
   index; one email = one login = one tenant). Future per-tenant path documented, not built.
3. Local dev migration = **start fresh + squash migrations** (drop dev `Negosio` DB; squash the 3
   Phase 1–3 migrations into one `TenantBaseline`; dev data is disposable).

## Completed Work

Phase 2.5 is **functionally complete**. Build is clean (0 warnings / 0 errors); full test suite green
(**144 tests: 50 unit + 94 integration**).

### Domain (`src/Negosio.Domain/`)
- New enums: `Enums/TenantProvisioningStatus.cs` (`Pending/Provisioning/Active/Failed/Suspended`),
  `Enums/TenantDatabaseStatus.cs` (`Pending/Active/Failed`).
- `Entities/Tenant.cs` **rewritten** → control-plane only: `Name, BusinessType, IsActive,
  ProvisioningStatus`; `IsOperational`; `MarkProvisioning/MarkActive/MarkFailed/Suspend/Reactivate`.
  Lost branches/users navs, `AddBranch/AddUser`, tax fields.
- New `Entities/Platform/TenantDatabase.cs`, `Entities/Platform/PlatformUserLogin.cs`
  (`Id == tenant User.Id`, `Normalize(email)`), `Entities/Tenant/TenantProfile.cs`
  (single row, `Id == TenantId`, `Name/BusinessType/TaxRatePercent/PricesIncludeTax`, `ConfigureTax`).
- `Entities/User.cs` **rewritten** → no `Tenant` nav, no `PasswordHash`/`SetPasswordHash`. New:
  `User.Create(Guid id, Guid tenantId, string normalizedEmail, string firstName, string lastName, UserRole role)`.

### Application (`src/Negosio.Application/`)
- New abstractions: `Abstractions/IPlatformDbContext.cs`, `ITenantDbContext.cs`, `ITenantContext.cs`,
  `ITenantConnectionResolver.cs` (+ `record TenantConnection`), `ITenantDbContextFactory.cs`,
  `ITenantDatabaseProvisioner.cs`.
- `Abstractions/IApplicationDbContext.cs` **deleted**.
- `Abstractions/IJwtTokenGenerator.cs` — added `record TokenSubject(Guid UserId, Guid TenantId,
  UserRole Role, string Email)` + `Generate(TokenSubject)`; `Generate(User)` is a default method.
- New `Platform/TenantProvisioningService.cs` — `ITenantProvisioningService` + records
  (`ProvisionTenantCommand`, `ProvisionBranchInput`, `ProvisionOwnerInput`, `ProvisionResult`).
  Idempotent, re-entrant cross-DB workflow.
- `Auth/AuthService.cs` **rewritten** — `RegisterAsync` delegates to `ITenantProvisioningService`;
  `LoginAsync` uses `IPlatformDbContext` + `ITenantDbContextFactory`; `GetCurrentUserAsync` /
  `BuildAuthUserDtoAsync` read the tenant DB. **Does NOT inject scoped `ITenantDbContext`** (see
  Important Decisions).
- `Common/AppException.cs` — added `TenantUnavailableException` (403), `TenantProvisioningException`
  (500). `Common/ErrorCodes.cs` — added `TENANT_NOT_FOUND`, `TENANT_UNAVAILABLE`, `TENANT_SUSPENDED`,
  `TENANT_PROVISIONING_FAILED`.
- `DependencyInjection.cs` — registers `ITenantProvisioningService`.
- **14 tenant services** bulk-swapped `IApplicationDbContext` → `ITenantDbContext`
  (Category, Product, ProductVariant, DocumentNumber, Dashboard, InventoryPosting, Inventory,
  Checkout, PosCatalog, Register, RegisterSession, Receipt, Return, SaleQuery, TenantSettings).
  `Dashboard/ReceiptService/TenantSettings/Checkout/Return` also swapped `_db.Tenants` →
  `_db.TenantProfiles`.

### Infrastructure (`src/Negosio.Infrastructure/`)
- New `Persistence/Tenant/TenantDbContext.cs` — `sealed`, **two constructors**: `(options)` →
  delegates to `(options, NullTenantContext.Instance)` for factory/provisioning/tests;
  `(options, ITenantContext)` for the DI request path (SaveChanges asserts every added/modified
  `TenantId` == `ITenantContext.TenantId`).
- New `Persistence/Platform/PlatformDbContext.cs` — `sealed`, history `__PlatformMigrationsHistory`.
- New configs: `Persistence/Platform/Configurations/{Tenant,TenantDatabase,PlatformUserLogin}Configuration.cs`
  (namespace `Negosio.Infrastructure.Persistence.Platform`);
  `Persistence/Configurations/TenantProfileConfiguration.cs` (namespace
  `Negosio.Infrastructure.Persistence.Configurations`). `Configurations/UserConfiguration.cs` edited
  (dropped PasswordHash + tenant FK index; kept per-DB unique email).
- **Deleted:** `Persistence/AppDbContext.cs`, `AppDbContextFactory.cs`,
  `Configurations/TenantConfiguration.cs`, the 3 old migrations + `AppDbContextModelSnapshot.cs`.
- New `Persistence/DesignTimeFactories.cs` — `PlatformDbContextDesignTimeFactory`
  (env `ConnectionStrings__Platform`), `TenantDbContextDesignTimeFactory` (env `NEGOSIO_DESIGN_TENANT_SQL`).
- New `Tenancy/` folder: `TenantConnectionAccessor.cs` (scoped, `Require()` throws),
  `TenantServerRegistry.cs` (singleton, reads `TenantDatabases:Servers:<key>` template),
  `TenantConnectionResolver.cs` (scoped, `IMemoryCache` 60 s TTL, throws 404/403 by state),
  `SqlDatabaseProvisioner.cs` (`sealed partial`, regex `^Negosio_Tenant_[A-F0-9]{8,64}$`, guarded
  `CREATE DATABASE`), `TenantDbContextFactory.cs`.
- `DependencyInjection.cs` **rewritten** — `AddDbContext<PlatformDbContext>` (fixed conn),
  `AddDbContext<TenantDbContext>` (per-request conn from `TenantConnectionAccessor.Require()`),
  memory cache, registry, resolver, provisioner, factory.
- `Security/JwtTokenGenerator.cs` — `Generate(TokenSubject)`.
- New migrations: `Persistence/Migrations/Platform/*_PlatformBaseline.cs` (+ `PlatformDbContextModelSnapshot.cs`),
  `Persistence/Migrations/Tenant/*_TenantBaseline.cs` (+ `TenantDbContextModelSnapshot.cs`).
  Namespaces `...Persistence.Migrations.PlatformDb` / `...TenantDb`.
  *(This session moved the two ModelSnapshot files out of a stray nested
  `src/Negosio.Infrastructure/Negosio/...` folder that `dotnet ef` had created; that folder is deleted.)*

### API (`src/Negosio.Api/`)
- New `Authentication/HttpTenantContext.cs` (`: ITenantContext`, reads `tenant_id` claim).
- New `Middleware/TenantResolutionMiddleware.cs` (after `UseAuthentication`, before `UseAuthorization`).
- `Program.cs` — registers `ITenantContext`; pipeline adds `TenantResolutionMiddleware`;
  `ApplyMigrationsAsync` migrates platform DB then iterates `TenantDatabases` rows and migrates each.
- `appsettings.json` / `appsettings.Development.json` — `ConnectionStrings:Default` replaced by
  `ConnectionStrings:Platform` + `TenantDatabases:DefaultServerKey` + `TenantDatabases:Servers:default`
  (a template: server + auth, **no** `Database=`). Dev = `(localdb)\MSSQLLocalDB`, platform DB
  `Negosio_Platform`.

### Tests (`tests/Negosio.IntegrationTests/`)
- `Infrastructure/NegosioApiFactory.cs` **rewritten** — per-run `Negosio_Test_Platform_<guid>`;
  real provisioning creates real `Negosio_Tenant_%` DBs; drops them + platform DB on dispose;
  `OpenTenantConnectionAsync(Guid tenantId)` for raw-SQL assertions.
- `Infrastructure/IntegrationTest.cs` **rewritten** — `InScopeAsync` → current tenant DB,
  `InTenantScopeAsync(Guid tenantId, …)`, `InPlatformScopeAsync`, `AddTenantUserTokenAsync(email, role)`,
  `ResetDatabaseAsync` drops tenant DBs + clears platform tables. `Authorize()` now guards
  `handler.CanReadToken` (for malformed/tampered-token tests).
- Edited to new helpers: `AuthorizationTests.cs`, `Catalog/ProductTests.cs`, `Sales/SaleQueryTests.cs`,
  `RegistrationTests.cs`.
- **New** `tests/Negosio.IntegrationTests/Platform/`: `PlatformSql.cs` (raw-SQL helpers),
  `ProvisioningTests.cs`, `TenantRoutingTests.cs`, `PhysicalIsolationTests.cs`, `TenantMigrationTests.cs`.
- `tests/Negosio.UnitTests/Domain/EntityTests.cs` — updated for new `Tenant` / `User` / `PlatformUserLogin` / `TenantProfile`.

### Docs
- `README.md` — new "Phase 2.5 — Database-per-Tenant Architecture" section; updated Architecture
  block, env-vars table, local-dev connection strings, EF-migrations section (two contexts),
  roadmap table.
- New ADRs: `docs/adr/0008-database-per-tenant.md`, `0009-platform-control-plane-database.md`,
  `0010-tenant-connection-resolution.md`, `0011-tenant-provisioning-lifecycle.md`,
  `0012-split-migration-histories.md`.
- `.env.example` — `ConnectionStrings__Platform` + `TenantDatabases__*`.

### Memory files (`C:\Users\Ace\.claude\projects\c--Users-Ace-Documents-Negosio\memory\`)
- New `phase-2_5-database-per-tenant.md` + index line in `MEMORY.md`.
- `phase-3-retail-pos.md` — corrected the now-moot `Tenant` tax-column `ValueGeneratedNever` gotcha.

## Current State

**Working / verified:**
- `dotnet build Negosio.sln --no-incremental` → 0 warnings, 0 errors.
- `dotnet test Negosio.sln` → 50 unit + 94 integration, all green (integration run ≈ 5m40s).
- `dotnet ef migrations list --context PlatformDbContext` and `--context TenantDbContext` both resolve
  (one baseline each, "Pending" against the design-time DBs).
- Dev `Negosio` DB dropped. Ran the real API (`dotnet run --project src/Negosio.Api`, Development):
  it created `Negosio_Platform` and applied `PlatformBaseline`. `POST /api/auth/register` created
  `Negosio_Tenant_789554E8…` (18 tables), seeded profile/branch/owner. `login` / `me` / `dashboard`
  work. A category created over HTTP landed in that tenant's DB; a second tenant's DB showed 0 rows
  (physical isolation confirmed by raw `sqlcmd`).
- Frontend: `npm run lint` (oxlint) clean, `npm run build` (`tsc -b && vite build`) succeeds. No
  frontend code was changed.

**Partially working / not done:**
- **Nothing is committed.** Working tree has all Phase 2.5 changes staged-as-unstaged (`git status`
  shows M/D + many `??`).
- Frontend end-to-end **manual smoke through the browser** (register A → catalog/inventory/POS/
  checkout/receipt/return → register B → confirm isolation) was **not performed** — only lint/build
  and API-level curl checks. *Needs verification.*
- The user explicitly asked to **pause for final architecture review** before any further work.

**Looks wrong / needs refinement:** none known. See Known Issues for minor notes.

## Known Issues / Bugs

- **60-second routing cache vs. suspension:** `TenantConnectionResolver` caches the route for 60 s, so
  suspending a tenant in the platform DB takes up to 60 s to take effect. This is intentional and
  documented (ADR 0010). `TenantRoutingTests` evicts the `IMemoryCache` key `tenant-route:{tenantId}`
  to test the path immediately.
- **Orphaned tenant database after a mid-provision failure:** if `CREATE DATABASE` succeeds but a
  later step fails, the physical DB remains and is reused on retry. No janitor for genuinely abandoned
  `Failed` tenants (documented in ADR 0011, out of scope for 2.5).
- **Stale dev server trap (environment, not code):** a VS Code / C# Dev Kit `dotnet run` process can
  keep the old shared `Negosio` DB open and serve the *old* schema. Kill any `Negosio.Api` / `dotnet
  run` process (by port or name) before verifying. Observed once this session — the process even
  reported a `C:\Users\Ace\Documents\Negocio\...` (misspelled) path; there is no such folder, only
  `C:\Users\Ace\Documents\Negosio`.
- **MARS savepoint warning** in logs during registration (`Savepoints are disabled because MARS is
  enabled`). Pre-existing, benign; the provisioning workflow rolls back explicitly.
- README intro paragraph still says "This repository contains **Phase 1**" — stale since Phase 2,
  out of scope this session. *Needs verification / low priority.*
- No screenshots taken this session.

## Important Decisions

- **Two `DbContext`s, separate migration histories, one assembly.** `PlatformDbContext`
  (`__PlatformMigrationsHistory`) + `TenantDbContext` (`__EFMigrationsHistory`, different physical DB).
  Configs separated by namespace predicate in `ApplyConfigurationsFromAssembly`.
- **`AuthService` must NOT depend on scoped `ITenantDbContext`.** DI eagerly constructs it, and
  `TenantConnectionAccessor.Require()` throws when no tenant is resolved — which is exactly the case
  on the anonymous `register` / `login` endpoints. `AuthService` uses `ITenantDbContextFactory`
  instead. **Do not "simplify" this back to injecting the context.**
- **`TenantDbContext` two-constructor pattern.** The parameterless-`ITenantContext` path
  (`NullTenantContext`) is for the factory (provisioning + tests, tenant guard off); the injected
  path is for requests (guard on). Keep both.
- **Connection strings composed server-side only** from `TenantDatabases:Servers:<key>` template +
  stored `DatabaseName`. No client input ever reaches a connection string. `CREATE DATABASE` names are
  server-generated, regex-validated, always bracket-quoted.
- **Password hash authority = platform DB only.** Tenant `User` has no hash.
- **Email stays globally unique** via `PlatformUserLogin.EmailNormalized`. Registration's
  `DUPLICATE_EMAIL` is raised by that index *before any tenant DB is created*.
- **Squashed the 3 Phase 1–3 tenant migrations into one `TenantBaseline`** (dev data disposable).
  Platform side gets one `PlatformBaseline`.
- **Rejected approaches:** EF global query filters on a shared DB; schema-per-tenant; sharding.
  Also rejected: `dotnet ef --output-dir Persistence/{Platform,Tenant}/Migrations` — it created a
  `Persistence.Tenant` namespace that **shadowed the `Tenant` domain class**. Correct form:
  `--output-dir Persistence/Migrations/{Platform,Tenant} --namespace ...Persistence.Migrations.{PlatformDb,TenantDb}`.
- **Provisioning is not one transaction** (cross-DB); it is an idempotent, re-entrant workflow with
  compensating `Failed` status. Re-POST same email resumes `Pending/Provisioning/Failed`; `Active`/
  `Suspended` → `409 DUPLICATE_EMAIL`.
- Kept every Phase 1–3 test (business assertions preserved), just re-pointed at the split contexts —
  per the user's instruction not to delete tests that merely fail after the architecture change.

## Files to Review

Start here to rebuild context, roughly in dependency order:

1. `docs/adr/0008-database-per-tenant.md` … `0012-split-migration-histories.md` — the "why".
2. `README.md` — "Phase 2.5" section.
3. `C:\Users\Ace\.claude\projects\c--Users-Ace-Documents-Negosio\memory\phase-2_5-database-per-tenant.md`
   — condensed decisions + gotchas.
4. `src/Negosio.Infrastructure/DependencyInjection.cs` — the whole wiring.
5. `src/Negosio.Api/Program.cs` + `src/Negosio.Api/Middleware/TenantResolutionMiddleware.cs` +
   `src/Negosio.Api/Authentication/HttpTenantContext.cs`.
6. `src/Negosio.Infrastructure/Tenancy/` (all 5 files).
7. `src/Negosio.Infrastructure/Persistence/Tenant/TenantDbContext.cs` +
   `Persistence/Platform/PlatformDbContext.cs`.
8. `src/Negosio.Application/Platform/TenantProvisioningService.cs` +
   `src/Negosio.Application/Auth/AuthService.cs`.
9. `src/Negosio.Domain/Entities/Tenant.cs`, `Entities/Platform/*`, `Entities/Tenant/TenantProfile.cs`,
   `Entities/User.cs`.
10. `tests/Negosio.IntegrationTests/Infrastructure/{NegosioApiFactory,IntegrationTest}.cs` +
    `tests/Negosio.IntegrationTests/Platform/*`.
11. `src/Negosio.Infrastructure/Persistence/Migrations/{Platform,Tenant}/*` — inspect the baselines.
12. `src/Negosio.Api/appsettings*.json` + `.env.example`.

## Next Steps

Prioritized. **The user asked to pause for final architecture review — do not start new
implementation work without confirming with them.**

1. **Present the Phase 2.5 result for review** (this is the pause point). Summarise the architecture,
   test results, and verification evidence. Wait for feedback.
2. **On the user's "commit" instruction:** `git add -A` and commit with a clear Phase 2.5 message.
   **Do NOT include a `Co-Authored-By: Claude` / `🤖 Generated with Claude Code` line** — the user
   has explicitly required this on every prior commit in this repo. Commit on `master`.
3. If review raises changes, apply them, then re-run `dotnet build Negosio.sln` (expect 0 warnings)
   and `dotnet test Negosio.sln` (expect 50 + 94 green).
4. **Frontend browser smoke** (regression only — no functional edits expected): start the API
   (creates `Negosio_Platform` + per-tenant DBs), run `cd web/negosio-web && npm run dev`, register
   tenant A, walk catalog → inventory → POS → checkout → receipt → return, then register tenant B and
   confirm isolation. Report any contract mismatch instead of patching around it.
5. Only after review sign-off: proceed to whatever the user names next (likely Phase 3 frontend, or a
   later phase). Phase 3 frontend is **not** started.

### Useful commands

```bash
# build + test
dotnet build Negosio.sln
dotnet test Negosio.sln

# run API (dev) — creates Negosio_Platform + tenant DBs on demand
dotnet run --project src/Negosio.Api          # http://localhost:5170, swagger at /swagger

# EF (always pass --context)
dotnet dotnet-ef migrations list --context PlatformDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api
dotnet dotnet-ef migrations list --context TenantDbContext   --project src/Negosio.Infrastructure --startup-project src/Negosio.Api

# inspect dev databases
sqlcmd -S "(localdb)\MSSQLLocalDB" -Q "SELECT name FROM sys.databases WHERE name LIKE 'Negosio%' ORDER BY name;"

# frontend
cd web/negosio-web && npm run lint && npm run build
```

Note: `sqlcmd -i <file>` failed this session with an "Access is denied" path error; use `-Q "<query>"`
inline instead.

## Prompt for Next Claude Session

```
You are continuing work on Negosio, a multi-tenant .NET 9 / EF Core / SQL Server + React SaaS
platform (modular monolith). Read these first, in order:
  1. handover.md (repo root) — full context for where we are.
  2. docs/adr/0008–0012 and the "Phase 2.5" section of README.md.
  3. C:\Users\Ace\.claude\projects\c--Users-Ace-Documents-Negosio\memory\phase-2_5-database-per-tenant.md

STATE: Phase 2.5 (Database-per-Tenant refactor) is functionally complete on branch `master` and
NOT committed. `dotnet build Negosio.sln` is clean (0 warnings); `dotnet test Negosio.sln` is green
(50 unit + 94 integration). The API was run and verified to create Negosio_Platform + one
Negosio_Tenant_<id> DB per registration, with physical isolation confirmed. Frontend lint + build
pass; no frontend code was changed.

The user asked to PAUSE for a final architecture review before any further work.

DO NOW:
  1. Give me a concise review-ready summary of the Phase 2.5 architecture and how it was verified,
     then ask what I'd like to change or whether to commit.
  2. Do NOT begin new implementation work or a frontend rewrite until I confirm.
  3. When I say "commit": `git add -A` + commit on master with a Phase 2.5 message and
     ABSOLUTELY NO "Co-Authored-By: Claude" or "Generated with Claude Code" line (repo convention).

GUARDRAILS — do not undo these deliberate decisions (see handover.md "Important Decisions"):
  - AuthService must NOT inject scoped ITenantDbContext (use ITenantDbContextFactory).
  - Keep TenantDbContext's two constructors (NullTenantContext path for factory/tests).
  - Connection strings are composed server-side only from config templates; never from client input.
  - Two EF contexts / two migration histories; migration namespaces are ...Migrations.PlatformDb /
    ...Migrations.TenantDb (never let a "Persistence.Tenant" namespace shadow the Tenant class).
  - Password hash lives only in PlatformUserLogin; email is globally unique there.

If you run the API and see the OLD single-DB schema, a stale `dotnet run` / Negosio.Api process is
serving it — kill it by port/name and retry.
```
