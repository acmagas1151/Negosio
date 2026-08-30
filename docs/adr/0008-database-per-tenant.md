# ADR 0008 — Database-per-tenant tenancy model

- Status: Accepted
- Date: 2026-08-29
- Phase: 2.5 (Database-per-Tenant Architecture)

## Context

Phases 1–3 stored every tenant in one shared operational database with row-level isolation: every
tenant-owned table carries `TenantId` and every query filters on it (`WHERE TenantId = @x`). This is
cheap to run but has structural weaknesses: a single missing filter leaks data across tenants, one
tenant's load and locks affect everyone, backup/restore and "export my data" are per-row surgery, and
a noisy tenant cannot be moved or throttled independently.

The alternatives considered: (a) keep shared-schema + add EF global query filters; (b) schema-per-tenant
in one database; (c) **database-per-tenant**; (d) shard by tenant hash. (a) still keeps everyone in one
blast radius. (b) multiplies schema objects and still shares the server's resources and transaction log.
(d) is premature — there is no scale problem yet.

## Decision

Each tenant gets its **own operational SQL database** (named
`Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>` — see [ADR 0011](0011-tenant-provisioning-lifecycle.md)),
containing all Phase 1–3 operational tables (branches, users, catalog, inventory, registers, sales,
returns, document-number counters) plus a single-row `TenantProfile`. A small **platform database** holds
tenant identity, database routing and the login directory (see [ADR 0009](0009-platform-control-plane-database.md)).

The application stays a modular monolith. Only *where rows physically live* changes:

- `AppDbContext` / `IApplicationDbContext` is split into `PlatformDbContext` / `IPlatformDbContext`
  (fixed connection) and `TenantDbContext` / `ITenantDbContext` (per-request connection).
- The 14 tenant-facing services swap `IApplicationDbContext` for `ITenantDbContext`; their queries are
  otherwise unchanged. `TenantId` stays as an indexed column on tenant tables (defence in depth and
  cheap future consolidation), but it is no longer a security boundary — the database boundary is.
- API contracts, controllers, DTOs and the React app are unchanged.

`TenantDbContext.SaveChangesAsync` still asserts that every added/modified entity's `TenantId` matches
the request's tenant — a guard, now backed by physical separation rather than being the only line.

## Consequences

- **Isolation is physical.** Tenant B's rows cannot appear in a query against Tenant A's database even
  if application code forgets a filter. Proven by tests that connect straight to each database.
- **Per-tenant operations become trivial:** backup/restore, point-in-time recovery, data export, and
  "delete this business" are single-database actions. A tenant can later be moved to another server by
  updating one routing row.
- **Blast radius shrinks:** a corrupt index, a long transaction, or a schema migration touches one
  tenant at a time.
- **Cost:** many databases to migrate on a schema change (see [ADR 0012](0012-split-migration-histories.md)),
  a per-request routing lookup ([ADR 0010](0010-tenant-connection-resolution.md)), cross-database
  provisioning that cannot be one transaction ([ADR 0011](0011-tenant-provisioning-lifecycle.md)), and
  no cross-tenant SQL query (acceptable — analytics is a later, separate concern).
- LocalDB / SQL Server handle hundreds of small databases comfortably; Azure SQL elastic pools are the
  production target and price exactly this shape.
