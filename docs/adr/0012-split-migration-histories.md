# ADR 0012 — Split migration histories

- Status: Accepted
- Date: 2026-08-29
- Phase: 2.5 (Database-per-Tenant Architecture)

## Context

Two `DbContext`s now target two physically different kinds of database — one platform database and
many tenant databases — with different schemas that evolve on independent timelines. EF Core needs a
migrations history it can apply and track for each.

## Decision

**Two separate migration histories in the one `Negosio.Infrastructure` assembly:**

| Context | History table | Migrations folder | Namespace |
| --- | --- | --- | --- |
| `PlatformDbContext` | `__PlatformMigrationsHistory` | `Persistence/Migrations/Platform` | `...Persistence.Migrations.PlatformDb` |
| `TenantDbContext` | `__EFMigrationsHistory` (default — different physical DB) | `Persistence/Migrations/Tenant` | `...Persistence.Migrations.TenantDb` |

`OnModelCreating` on each context calls `ApplyConfigurationsFromAssembly` with a namespace predicate,
so platform and tenant entity configurations never bleed into each other.

The three Phase 1–3 migrations (`InitialCreate`, `Phase2CatalogInventory`, `Phase3RetailPos`) and the
old `AppDbContextModelSnapshot` were **squashed into one `TenantBaseline`** = the full Phase 1–3
operational schema minus `Tenants`, plus `TenantProfile`. Local dev data was disposable (two empty
test tenants), so this is a clean cut rather than a data migration. The platform side gets one
`PlatformBaseline` (three tables).

Applying migrations:
- **Platform:** `Database:MigrateOnStartup=true` migrates `Negosio_Platform` on API boot; the design-
  time factory (`ConnectionStrings__Platform` env var) drives `dotnet ef`.
- **Tenant:** every `CREATE DATABASE` in provisioning is followed by `MigrateAsync()`. On API boot
  (when `MigrateOnStartup`), after the platform migrate, the startup path iterates every
  `TenantDatabases` row and runs `MigrateAsync()` against it — a simple built-in migration runner. A
  dedicated design-time tenant database (`NEGOSIO_DESIGN_TENANT_SQL`) exists only for `dotnet ef`
  scaffolding.

The `dotnet ef` output-dir must not create a `...Persistence.Tenant.Migrations` namespace — that made
`Persistence.Tenant` shadow the `Tenant` domain class. Hence the explicit `PlatformDb` / `TenantDb`
namespaces and `Migrations/{Platform,Tenant}` folders.

## Consequences

- Platform and tenant schemas version independently; a platform change never forces a tenant migration
  or vice versa.
- A future tenant schema change is one new migration in `Migrations/Tenant`, applied to every tenant
  database by the boot-time loop and by provisioning for new tenants.
- **Cost:** a schema change is O(number of tenants) database updates, run serially on boot. Fine for
  now; a resumable, parallel, out-of-band migration runner (with per-tenant status and rollout
  control) is a scaling-phase concern.
- Squashing discarded the Phase 1–3 migration granularity for tenant databases. Acceptable — no
  production tenant data existed. Future history is append-only again.
