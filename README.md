# Negosio

Negosio is a multi-tenant SaaS platform for retail and food & beverage businesses. A business owner
signs up, picks their business type, describes their first branch, creates an owner account, and lands
in a dashboard scoped strictly to their own tenant.

This repository contains **Phase 1: the SaaS foundation** — registration, authentication, tenant
isolation, and the supporting infrastructure. Vertical features (catalog, POS, ordering, …) are
deliberately out of scope and land in later phases.

---

## Supported business types

| Business type       | Status                        |
| ------------------- | ----------------------------- |
| Retail              | ✅ Available                   |
| Food & Beverage     | ✅ Available                   |
| Diagnostic Center   | 🕓 Coming soon (not selectable) |

`DiagnosticCenter` exists in the domain model, but registration rejects it with a clear error
(`BUSINESS_TYPE_NOT_AVAILABLE`). The web UI shows the option as a disabled "Coming Soon" card, and the
**backend enforces the rule independently** — it does not rely on the frontend.

---

## Architecture

A **modular monolith** with Clean Architecture influences, applied only where they pay for themselves.

```
Negosio.Api            ASP.NET Core Web API — controllers, auth wiring, middleware (incl. tenant resolution), HTTP concerns
Negosio.Application    Use cases (AuthService, DashboardService, TenantProvisioningService), DTOs, validators, error types,
                       abstractions (ICurrentUser, IPlatformDbContext, ITenantDbContext, ITenantConnectionResolver,
                       ITenantDbContextFactory, IPasswordHasher, IJwtTokenGenerator)
Negosio.Infrastructure Platform + tenant EF Core DbContexts + configurations, split migrations, tenant routing,
                       SQL database provisioning, password hashing, JWT generation
Negosio.Domain         Entities (Tenant, TenantDatabase, PlatformUserLogin, TenantProfile, Branch, User, …) and enums — no dependencies
```

As of **Phase 2.5** the persistence layer is **database-per-tenant**: a small platform (control-plane)
database plus one operational database per tenant. See the Phase 2.5 section below and
[ADR 0008–0012](docs/adr/).

Dependency direction: `Api → Infrastructure → Application → Domain` (Api also references Application/Domain directly).

Deliberately **not** used in Phase 1: microservices, a generic repository, MediatR/CQRS, event buses,
and any Azure service. See the roadmap for when those become relevant.

### Multi-tenancy

* Every tenant-owned entity carries a `TenantId`.
* The authenticated user is the **only** source of the current `TenantId`. `ICurrentUser` reads it from
  the validated JWT (`HttpCurrentUser` in the API). Tenant-owned endpoints never accept a client-supplied
  `TenantId`.
* The dashboard and `/auth/me` queries are filtered by `ICurrentUser.TenantId`.

### Registration is atomic

`POST /api/auth/register` creates the Tenant, its first Branch, and the Owner user inside **one
database transaction**. If any step fails (for example the owner email is already taken), the whole
transaction rolls back and no partial tenant is left behind. Uniqueness is enforced by database
indexes rather than a read-then-write check, so there is no race window; a unique-violation is
translated into a `409 DUPLICATE_EMAIL` / `409 DUPLICATE_BRANCH_CODE` response.

### Email uniqueness (Phase 1 decision)

For Phase 1, **email is globally unique** (`IX_Users_Email`). This keeps login simple: given an email
and password we can find exactly one user. The trade-off is that the same person cannot use one email
address across two different businesses.

A later phase can relax this to *unique per tenant* by switching the index to `(TenantId, Email)` and
adding a tenant selector to the login flow (e.g. choose-your-workspace, or a tenant slug in the URL).
The domain already normalizes emails (`User.NormalizeEmail`) so the change is localized.

### Post-registration flow

Registration returns `201 Created` with the new ids and **no token**. The web app then redirects to
`/login` with the email pre-filled. Rationale: a single code path issues tokens (login), the user
immediately confirms their credentials work, and the registration endpoint stays free of session
concerns. The cost is one extra form submit right after signup, which is acceptable for a
once-per-business action.

---

## Tech stack

* **Backend:** C#, .NET 9, ASP.NET Core Web API, EF Core 9, SQL Server, JWT bearer auth,
  ASP.NET Core `PasswordHasher` (PBKDF2), FluentValidation, Serilog.
* **Tests:** xUnit, FluentAssertions, `WebApplicationFactory`, Testcontainers (SQL Server) with a
  LocalDB fallback.
* **Frontend:** React 19, TypeScript, Vite, React Router, TanStack Query.
* **Local infra:** Docker Compose (SQL Server).
* **Future production target:** Azure — Azure SQL Database, App Service / Container Apps.

---

## Project structure

```
Negosio/
├── src/
│   ├── Negosio.Api/               # Web API host
│   ├── Negosio.Application/       # use cases, DTOs, validation, abstractions
│   ├── Negosio.Domain/            # entities + enums
│   └── Negosio.Infrastructure/    # EF Core, migrations, security adapters
├── tests/
│   ├── Negosio.UnitTests/         # domain + validator tests
│   └── Negosio.IntegrationTests/  # full HTTP + real SQL Server tests
├── web/
│   └── negosio-web/               # React + Vite SPA
├── docker-compose.yml             # local SQL Server
├── .env.example
└── README.md
```

---

## Prerequisites

* .NET SDK 9
* Node.js 20+ and npm
* One of:
  * Docker Desktop (for `docker-compose` SQL Server and Testcontainers), **or**
  * SQL Server LocalDB (ships with Visual Studio / SQL Server Express) for a Docker-free setup on Windows.

---

## Environment variables

Copy `.env.example` to `.env` and adjust. `.env` is git-ignored. Never commit real secrets.

| Variable | Used by | Notes |
| --- | --- | --- |
| `MSSQL_SA_PASSWORD` | docker-compose | SA password for the SQL Server container |
| `MSSQL_PORT` | docker-compose | Host port for SQL Server (default `1433`) |
| `ConnectionStrings__Platform` | API | EF Core connection string for the platform (control-plane) database |
| `TenantDatabases__DefaultServerKey` | API | Server key new tenant databases are provisioned onto (default `default`) |
| `TenantDatabases__Servers__default` | API | Connection-string **template** (server + auth, no `Database=`) for that server key |
| `Jwt__Issuer`, `Jwt__Audience` | API | Token issuer/audience |
| `Jwt__SigningKey` | API | **Secret.** Symmetric signing key, ≥ 32 bytes |
| `Jwt__AccessTokenMinutes` | API | Access-token lifetime (default `60`) |
| `Database__MigrateOnStartup` | API | Apply EF migrations on boot (`true` for local/dev) |
| `Cors__AllowedOrigins__0` | API | Allowed SPA origin (default `http://localhost:5173`) |
| `VITE_API_BASE_URL` | web | API base URL (default `http://localhost:5170`) |

`src/Negosio.Api/appsettings.Development.json` contains a **throwaway** signing key and a LocalDB
connection string so the project runs with zero setup. These are not production secrets; production
must supply real values via environment variables or a secret store.

---

## Local development

### Option A — Docker Compose (SQL Server in a container)

```bash
cp .env.example .env            # then edit MSSQL_SA_PASSWORD etc.
docker compose up -d            # starts SQL Server with a healthcheck + persistent volume
```

Point the API at it (in `.env` or user-secrets) — the platform database plus a server template that
tenant databases are provisioned onto:

```
ConnectionStrings__Platform=Server=localhost,1433;Database=Negosio_Platform;User Id=sa;Password=<your password>;TrustServerCertificate=true;MultipleActiveResultSets=true
TenantDatabases__DefaultServerKey=default
TenantDatabases__Servers__default=Server=localhost,1433;User Id=sa;Password=<your password>;TrustServerCertificate=true;MultipleActiveResultSets=true
```

### Option B — SQL Server LocalDB (no Docker, Windows)

The default `appsettings.Development.json` already targets
`Server=(localdb)\MSSQLLocalDB;Database=Negosio_Platform;...` for the platform database and the same
LocalDB instance as the tenant server template. Just make sure LocalDB is running:

```bash
sqllocaldb start MSSQLLocalDB
```

---

## EF Core migrations

`dotnet-ef` is pinned as a local tool (`dotnet tool restore` first if needed). There are **two
contexts with separate histories** ([ADR 0012](docs/adr/0012-split-migration-histories.md)) — always
pass `--context`.

```bash
# platform (control-plane) database — history table __PlatformMigrationsHistory
dotnet dotnet-ef migrations add <Name> --context PlatformDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Platform --namespace Negosio.Infrastructure.Persistence.Migrations.PlatformDb

# tenant (operational) database — one schema applied to every tenant DB
dotnet dotnet-ef migrations add <Name> --context TenantDbContext \
  --project src/Negosio.Infrastructure --startup-project src/Negosio.Api \
  --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb

dotnet dotnet-ef migrations list --context PlatformDbContext --project src/Negosio.Infrastructure --startup-project src/Negosio.Api
```

Baselines: `Persistence/Migrations/Platform/*_PlatformBaseline.cs` and
`Persistence/Migrations/Tenant/*_TenantBaseline.cs`.

With `Database__MigrateOnStartup=true` the API, on boot, migrates the platform database and then
iterates every `TenantDatabases` row and migrates each tenant database. New tenants are migrated by
the provisioning workflow right after `CREATE DATABASE`.

---

## Run the backend

```bash
dotnet restore
dotnet build
dotnet run --project src/Negosio.Api
```

* API: `http://localhost:5170`
* Swagger UI (Development only): `http://localhost:5170/swagger`

### Endpoints

| Method & path | Auth | Purpose |
| --- | --- | --- |
| `POST /api/auth/register` | anonymous | Create tenant + first branch + owner (one transaction) |
| `POST /api/auth/login` | anonymous | Exchange credentials for a JWT + user profile |
| `GET  /api/auth/me` | authenticated | Current user, tenant, role, business type (for SPA reload) |
| `GET  /api/dashboard` | authenticated | Tenant info + branch/user counts (own tenant only) |
| `GET  /api/admin/owner-test` | Owner only | RBAC smoke test |

### Error format

Every failure returns a consistent envelope:

```json
{ "code": "DUPLICATE_EMAIL", "message": "An account with this email already exists.", "traceId": "..." }
```

Validation failures add an `errors` map (`field -> messages`). Status codes: `400` validation/business
rule, `401` unauthenticated, `403` forbidden, `404` not found, `409` conflict, `500` unexpected
(stack traces are never exposed outside Development).

### Logging

Structured logging via Serilog. Request logs include trace id, method, path, status, and duration.
Passwords, password hashes, and tokens are never logged.

---

## Run the frontend

```bash
cd web/negosio-web
npm install
cp .env.example .env.local        # optional; defaults to http://localhost:5170
npm run dev                       # http://localhost:5173
```

Routes: `/register` (5-step onboarding wizard), `/login`, `/dashboard` (protected). The API layer is
centralized in `src/api/` (typed client + endpoints), server state is managed with TanStack Query, and
auth state lives in a small `AuthProvider` (no Redux).

### Authentication storage & security trade-offs

Phase 1 stores the JWT in `localStorage`:

* **Pro:** simple, survives refresh, no cookie/CSRF plumbing, works with a stateless API.
* **Con:** readable by any JavaScript on the origin, so it is exposed to XSS. There is no refresh
  token and no server-side revocation. `localStorage` is **not** a secure secret store.

A hardened production design should move to: short-lived access tokens kept **in memory**, a refresh
token in a `Secure; HttpOnly; SameSite` cookie, server-side session/refresh-token revocation, and a
strict Content-Security-Policy. The `tokenStorage` module is the single seam to change.

---

## Run the tests

```bash
dotnet test
```

* **Unit tests** (`Negosio.UnitTests`): domain invariants and the registration validator. No database.
* **Integration tests** (`Negosio.IntegrationTests`): boot the real API with `WebApplicationFactory`
  and hit it over HTTP against a **real SQL Server**. Database selection order:
  1. `NEGOSIO_TEST_SQL` environment variable (explicit connection string), else
  2. a disposable SQL Server container via Testcontainers (needs Docker), else
  3. a uniquely-named LocalDB database (Windows, Docker-free) that is dropped afterwards.

  These tests exercise migrations, unique constraints, and the registration transaction — they do not
  mock `DbContext`.

Coverage includes: Retail/F&B registration succeeds; DiagnosticCenter rejected; tenant/branch/owner
created; owner gets the Owner role; duplicate email → 409; weak password → 400; **registration
rollback** leaves no partial data; valid/invalid/unknown-email login; JWT contains `tenant_id`;
`/auth/me` and `/dashboard` require auth and are tenant-scoped; owner-only endpoint allows Owner and
forbids non-Owner; malformed/tampered tokens are rejected.

### Frontend checks

```bash
cd web/negosio-web
npm run lint          # oxlint
npx tsc -b            # type-check
npm run build         # production build must succeed
```

---

## Docker Compose

`docker-compose.yml` runs a single official `mcr.microsoft.com/mssql/server` container with:

* a published port (`${MSSQL_PORT:-1433}:1433`),
* environment-driven configuration (`ACCEPT_EULA`, `MSSQL_SA_PASSWORD`, `MSSQL_PID=Developer`),
* a persistent named volume (`negosio-sql-data`),
* a `sqlcmd`-based healthcheck.

The API's connection string always comes from configuration — the container is just a database.

---

## Phase 1 scope

**In:** solution structure; domain entities + enums; EF Core configurations, indexes, and the initial
migration; registration (atomic) with full validation and the DiagnosticCenter rule; login; JWT
issuance from configuration; `ICurrentUser` HTTP implementation; RBAC (authenticated + Owner-only
policy); `/auth/me`; `/dashboard`; centralized error handling; structured logging; Docker Compose for
SQL Server; the React app (onboarding wizard, login, protected dashboard) wired to the API; unit and
integration tests.

**Out (later phases):** POS, restaurant ordering, kitchen display, purchasing, advanced inventory,
catalog/menu, sales metrics, Azure services, event bus, refresh tokens/cookie sessions.

---

## Phase 2 — Catalog + Inventory

Phase 2 adds the shared commerce foundation that later backs both Retail POS and F&B ordering:
**Categories, Products, sellable items** and **branch-aware Inventory** with an audit ledger. No POS
checkout, payments, kitchen, recipes, purchasing or reporting yet. Phase 1 architecture, auth and
tenant context are unchanged — this is additive (new entities, one migration, new services +
controllers, new React screens).

### Product vs sellable item

`Product` is the catalog-level definition (`Category`, `Name`, `Description`, `TrackInventory`).
Price, SKU and barcode live on **`ProductVariant`** — the *sellable item* — and every product owns
at least one. A product with no user-defined variants has a single hidden `IsDefault` variant that
the API hides and the product form edits inline; adding the first real variant promotes that row.
This keeps SKU/barcode uniqueness on **one table** with two filtered unique indexes
(`(TenantId, SKU) WHERE SKU IS NOT NULL`, and the same for barcode). Multi-variant products report
`minSellingPrice` / `maxSellingPrice`; price sorting uses the minimum active selling price. See
[ADR 0001](docs/adr/0001-single-sellable-item-model.md).

### Branch-based inventory

Quantity is never on `Product`. `BranchInventory` is keyed by `(TenantId, BranchId, ProductVariantId)`
(unique index) and carries `QuantityOnHand` and `ReorderLevel` as **`decimal(18,3)`** — piece counts
fit exactly and future weight/volume units need no schema change (conversion is out of scope). Money
stays `decimal(18,2)`. Low stock is `QuantityOnHand <= ReorderLevel`, surfaced by
`GET /api/inventory?lowStock=true`. See [ADR 0002](docs/adr/0002-inventory-separate-from-product.md).

### Stock movements

Every change to `QuantityOnHand` writes one append-only `StockMovement` (who / what / when / why /
before / after) inside the same transaction as the inventory update. `StockMovementType` covers
future workflows; Phase 2 produces only `OpeningStock`, `AdjustmentIncrease`, `AdjustmentDecrease`.
All stock changes funnel through `InventoryService.AdjustAsync`.

### Optimistic concurrency

`BranchInventory.RowVersion` is a SQL Server `rowversion` EF concurrency token. Inventory reads
return `concurrencyToken` (Base64). Adjusting an **existing** row requires the expected token — a
stale token returns `409 INVENTORY_CONCURRENCY_CONFLICT` and the row is left untouched; a missing or
malformed token returns `400`. Opening stock (row does not exist yet) needs no token. See
[ADR 0003](docs/adr/0003-rowversion-for-stock-concurrency.md).

### Tenant isolation & authorization

Every catalog/inventory query filters by `ICurrentUser.TenantId`; request ids are never trusted.
Writes are gated by role: `CatalogWrite` (Owner/Admin/Manager) for catalog mutations, `InventoryWrite`
(+ InventoryStaff) for adjustments. Reads are open to any authenticated tenant user, but **cost price
is redacted** for roles outside Owner/Admin/Manager/InventoryStaff.

### Endpoints

| Method & path | Auth | Purpose |
| --- | --- | --- |
| `GET/POST /api/categories`, `GET/PUT/DELETE /api/categories/{id}` | read: any; write: CatalogWrite | Categories (DELETE deactivates) |
| `GET/POST /api/products`, `GET/PUT/DELETE /api/products/{id}` | read: any; write: CatalogWrite | Products; list supports `search, categoryId, isActive, trackInventory, page, pageSize, sortBy, sortDirection` |
| `GET/POST /api/products/{id}/variants`, `PUT/DELETE .../{variantId}` | read: any; write: CatalogWrite | User-defined variants |
| `GET /api/inventory`, `GET /api/inventory/{id}` | authenticated | Inventory rows (`branchId, productId, categoryId, search, lowStock, page, pageSize`) |
| `POST /api/inventory/adjustments` | InventoryWrite | The single inventory mutation path |
| `GET /api/inventory/movements` | authenticated | Ledger (`branchId, productId, productVariantId, type, fromUtc, toUtc, page, pageSize`) |

All list endpoints return `{ items, page, pageSize, totalCount, totalPages }` with `pageSize`
clamped to 100.

### Key indexes

`Categories (TenantId, NormalizedName)` unique · `Products (TenantId, {CategoryId|Name|IsActive})` ·
`ProductVariants (TenantId, ProductId)` and filtered-unique `(TenantId, SKU)` / `(TenantId, Barcode)` ·
`BranchInventories (TenantId, BranchId, ProductVariantId)` unique plus `(TenantId, BranchId)` and
`(TenantId, ProductVariantId)` · `StockMovements (TenantId, BranchId, CreatedAtUtc)` and
`(TenantId, ProductVariantId, CreatedAtUtc)`.

### Migration

`Persistence/Migrations/*_Phase2CatalogInventory` — creates the 5 tables only; Phase 1 tables and
data are untouched.

---

## Phase 3 — Retail POS (backend)

Phase 3 adds a reliable retail sales workflow on top of the Phase 2 catalog and inventory: POS
terminals, cash-drawer sessions, a cashier-only catalog, a strongly-consistent checkout, receipts,
sales history and basic returns/refunds. No restaurant tables, kitchen, recipes, suppliers,
transfers, payment gateways or e-commerce. Phase 1/2 architecture, auth and the inventory ledger are
reused unchanged.

### Registers & sessions

`Register` = a physical/logical till at a branch (`Code` unique per branch). A cashier opens a
`RegisterSession` with a starting cash count and rings sales against it; closing records the
expected cash (`OpeningCash` + cash payments − cash refunds for the session) and the difference. A
**filtered unique index** (`RegisterId WHERE Status = Open`) enforces at most one open session per
register. `POST /api/register-sessions/open`, `POST /api/register-sessions/{id}/close`,
`GET /api/register-sessions/current`.

### Checkout — one transaction

`POST /api/pos/checkout` completes a sale. Everything below commits together or rolls back entirely:
`Sale` + `SaleItem`s + `Payment`s + inventory deductions + `StockMovement.Sale` rows. The backend
**recalculates every amount** — the request carries no prices or totals, only variant ids,
quantities and discounts; unit prices come from `ProductVariant.SellingPrice`. Duplicate variant
lines are merged into one summed quantity.

### Idempotency

The client sends one `clientRequestId` (GUID) per checkout attempt and reuses it on retry.
`Sale.ClientRequestId` is unique per tenant; a repeat returns the **existing** sale (HTTP 200,
`wasExistingRequest: true`) — no second sale, payment or deduction. See
[ADR 0004](docs/adr/0004-checkout-idempotency.md).

### Inventory concurrency

POS deduction is an **atomic conditional UPDATE** (`... WHERE QuantityOnHand >= @qty`) inside the
checkout transaction — 0 rows affected ⇒ `409 INSUFFICIENT_INVENTORY` and rollback. Two checkouts
for the last unit: exactly one succeeds, stock lands at 0, never negative, one ledger movement. No
UI concurrency token (unlike the Phase 2 management adjustment). See
[ADR 0006](docs/adr/0006-atomic-inventory-deduction.md).

### Sale numbers

Human-readable, contiguous per `(tenant, branch, document type)`, allocated with a
`DocumentNumberCounters` row via `UPDATE ... OUTPUT INSERTED.LastNumber` in the checkout
transaction — never `COUNT(*)+1`, never reused after a void/refund. Format `INV-MAIN-000001` /
`RET-MAIN-000001`. See [ADR 0007](docs/adr/0007-sale-numbering-strategy.md).

### Snapshots

`SaleItem` stores transaction-time snapshots of name, SKU, barcode, unit price, computed amounts and
cost. Receipts and reports read the snapshots, so a later rename or reprice never alters an old
sale. Margin uses `CostPriceSnapshot`, never the current cost. See
[ADR 0005](docs/adr/0005-sale-item-snapshots.md).

### Payments

`Payment` is a separate table (many per sale allowed — split tender is schema-supported even if the
Phase 3 UI sends one). Cash payments carry `ReceivedAmount` and compute change; the payment total
must cover the grand total (`PAYMENT_INSUFFICIENT` otherwise). No gateway integration.

### Tax

Minimal tenant-level setting on `TenantProfile` (was `Tenant` before Phase 2.5): `TaxRatePercent`
(default 0) and `PricesIncludeTax` (default false → **tax-exclusive**), editable via
`GET`/`PUT /api/settings/tax` (Owner/Admin). Tax
is computed backend-side per line; the two modes are never mixed. Money is `decimal(18,2)`, rounded
**half-up (away from zero) at 2 dp**, per line then summed.

### Returns

`POST /api/sales/{id}/returns` (Owner/Admin/Manager) creates a `SaleReturn` + `SaleReturnItem`s +
`RefundPayment` in one transaction, bumps `SaleItem.ReturnedQuantity` (cannot exceed purchased −
already-returned → `RETURN_QUANTITY_EXCEEDED`), restocks inventory-tracked lines
(`StockMovement.Return`) and moves `Sale.Status` to `Refunded` / `PartiallyRefunded`. Void (reverse
a whole sale) is a documented later extension.

### Endpoints & authorization

| Area | Endpoints | Roles |
| --- | --- | --- |
| Registers | `GET/POST/PUT/DELETE /api/registers` | read: any; write: Owner/Admin/Manager |
| Sessions | `POST /api/register-sessions/open`, `.../{id}/close`, `GET .../current` | Owner/Admin/Manager/Cashier |
| POS catalog | `GET /api/pos/catalog`, `GET /api/pos/catalog/barcode/{barcode}` | Owner/Admin/Manager/Cashier — **no cost fields** |
| Checkout | `POST /api/pos/checkout` | Owner/Admin/Manager/Cashier |
| Sales | `GET /api/sales`, `GET /api/sales/{id}`, `GET /api/sales/{id}/receipt` | Owner/Admin/Manager/Cashier — cost redacted below Manager |
| Returns | `GET/POST /api/sales/{id}/returns` | read: sales roles; create: Owner/Admin/Manager |
| Tax settings | `GET/PUT /api/settings/tax` | read: any; write: Owner/Admin |

Dashboard gains `TodaysSales`, `TodaysTransactions`, `AverageTransactionValue` (SQL aggregates only).

### Migration

Phase 3 shipped as `*_Phase3RetailPos` (9 new tables plus two columns on `Tenants`). Phase 2.5 later
squashed all Phase 1–3 tenant migrations into a single `TenantBaseline` and moved the tax columns to
`TenantProfile` — see below.

---

## Phase 2.5 — Database-per-Tenant Architecture

A persistence-only refactor: move the physical tenancy boundary from *row-level* isolation in one
shared database to **one operational database per tenant**, plus a small **platform (control-plane)
database**. No business-logic rewrite, no microservices, no new infrastructure. All Phase 1–3
behaviour and every API contract are unchanged, so the React app needed no functional edits.

Full rationale and trade-offs: **[ADR 0008](docs/adr/0008-database-per-tenant.md)** (tenancy model) ·
**[0009](docs/adr/0009-platform-control-plane-database.md)** (platform database) ·
**[0010](docs/adr/0010-tenant-connection-resolution.md)** (connection resolution) ·
**[0011](docs/adr/0011-tenant-provisioning-lifecycle.md)** (provisioning lifecycle) ·
**[0012](docs/adr/0012-split-migration-histories.md)** (split migration histories).

### Two databases, two contexts

| | Platform database (`Negosio_Platform`, fixed) | Tenant database (`Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>`, one per tenant) |
| --- | --- | --- |
| Context | `PlatformDbContext` / `IPlatformDbContext` | `TenantDbContext` / `ITenantDbContext` |
| Connection | `ConnectionStrings:Platform` | resolved per request from the `tenant_id` claim |
| Tables | `Tenants`, `TenantDatabases`, `PlatformUserLogins` | `TenantProfile` + all Phase 1–3 operational tables (branches, users, catalog, inventory, registers, sales, returns, counters) |
| Migration history | `__PlatformMigrationsHistory` | `__EFMigrationsHistory` |

`Tenant` is now a control-plane record only (`Name`, `BusinessType`, `IsActive`,
`ProvisioningStatus`). Its branches, users and tax settings live in the tenant database
(`TenantProfile` holds name / business type / tax). `User` **no longer carries a password hash** — the
hash lives solely in `PlatformUserLogin`, which also holds the globally-unique email directory.

### Request pipeline

`UseAuthentication` → **`TenantResolutionMiddleware`** → `UseAuthorization` → controllers. The
middleware reads the `tenant_id` claim (`ITenantContext`), calls `ITenantConnectionResolver`
(platform-DB lookup joining `Tenants` + `TenantDatabases`, cached in `IMemoryCache` for 60 s) and
stashes the composed connection string in a scoped `TenantConnectionAccessor` that
`AddDbContext<TenantDbContext>` reads. Anonymous requests (register, login, swagger) pass straight
through. Connection strings are **composed server-side** from a configured per-server template
(`TenantDatabases:Servers:<key>`) + the stored `DatabaseName`; no client input ever reaches them.

Not-usable tenants fail before any handler runs: `404 TENANT_NOT_FOUND` (unknown),
`403 TENANT_SUSPENDED` (suspended / soft-disabled), `403 TENANT_UNAVAILABLE` (still provisioning or
failed).

### Registration → provisioning

`POST /api/auth/register` is unchanged on the wire but now runs `ITenantProvisioningService`, an
**idempotent, re-entrant** cross-database workflow: platform rows (`Tenant` `Pending` +
`PlatformUserLogin` + `TenantDatabase` `Pending`) in one platform transaction → `CREATE DATABASE`
(name `Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>` — business name + initial branch code sanitized
to `[A-Za-z0-9]`, an 8-hex tenant-id suffix for uniqueness; composed server-side, regex-validated,
bracket-quoted) → `MigrateAsync` → seed `TenantProfile` +
first branch + owner `User` → mark both `Active`. A failure marks the tenant `Failed` and returns
`500 TENANT_PROVISIONING_FAILED`; **re-POSTing the same email resumes** a `Pending`/`Provisioning`/
`Failed` tenant, while a finished (`Active`/`Suspended`) account returns `409 DUPLICATE_EMAIL`.
[ADR 0011](docs/adr/0011-tenant-provisioning-lifecycle.md).

### Login

`PlatformUserLogins` lookup by normalized email → timing-equalised hash verify → check
`Tenant.IsOperational` + `login.IsActive` → mint the JWT (claims identical to Phase 1) → a short-lived
`TenantDbContext` fills the user's name / business type into the response. Login never depends on a
tenant database being reachable.

### Tests

`NegosioApiFactory` boots the real API against one SQL Server: it creates a per-run platform database
and lets the **real provisioning workflow** create a real database per registered tenant, dropping
every `Negosio.%` and the platform DB on dispose. `IntegrationTest` gains
`InPlatformScopeAsync` / `InTenantScopeAsync(tenantId, …)` and a raw `SqlConnection` per tenant.
New suites under `tests/Negosio.IntegrationTests/Platform/`:

- **`ProvisioningTests`** — control-plane rows + a dedicated physical DB with the baseline applied;
  each tenant in its own DB; failed-tenant retry resumes idempotently; finished account rejects a
  duplicate email.
- **`TenantRoutingTests`** — writes land only in the caller's DB (verified by raw connection); unknown
  tenant → 404; suspended tenant → 403 with no tenant connection opened.
- **`PhysicalIsolationTests`** — connect straight to each tenant DB and prove the other tenant's
  catalog / profile rows are entirely absent.
- **`TenantMigrationTests`** — a freshly provisioned DB has no pending migrations; re-running the
  migrator is a no-op.

All Phase 1–3 tests were kept (business assertions preserved, re-pointed at the split contexts).

### Local migration note

Dev data was disposable, so Phase 2.5 dropped the old shared `Negosio` database and squashed the three
Phase 1–3 tenant migrations into one `TenantBaseline`. On next run the API creates `Negosio_Platform`
and each `POST /api/auth/register` creates that tenant's database.

---

## Future roadmap

| Phase | Focus |
| --- | --- |
| **Phase 2** ✅ | Catalog + Inventory |
| **Phase 2.5** ✅ | Database-per-tenant architecture (platform control-plane DB + one operational DB per tenant) |
| **Phase 3** 🚧 | Retail POS (backend complete) |
| **Phase 4** | Food & Beverage ordering + Kitchen |
| **Phase 5** | Purchasing + Multi-branch |
| **Phase 6** | Azure Service Bus, Functions, Redis, Blob Storage, Application Insights |
| **Phase 7** | Azure production deployment + CI/CD + Infrastructure as Code |
