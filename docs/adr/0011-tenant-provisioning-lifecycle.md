# ADR 0011 — Tenant provisioning lifecycle

- Status: Accepted
- Date: 2026-08-29
- Phase: 2.5 (Database-per-Tenant Architecture)

## Context

Phase 1 registration was one EF transaction: insert `Tenant` + `Branch` + `User`, commit or roll back.
With database-per-tenant, registration now spans **two databases plus a `CREATE DATABASE`** — the
platform rows, a new physical database, its schema migration, and the seed rows inside it. That cannot
be a single transaction, so a failure can leave partial state, and the client may retry.

## Decision

`ITenantProvisioningService.ProvisionAsync(ProvisionTenantCommand)` is an explicit, **idempotent,
re-entrant** workflow. `AuthService.RegisterAsync` delegates to it; `RegisterRequest` in /
`RegisterResponse(TenantId, BranchId, OwnerUserId)` out are unchanged.

`Tenant.ProvisioningStatus`: `Pending → Provisioning → Active`, or `Failed`, or `Suspended`.
`TenantDatabase.Status`: `Pending → Active`, or `Failed`.

Happy path:

1. **Platform transaction:** insert `Tenant` (`Pending`), `PlatformUserLogin` (unique email — this is
   where `DUPLICATE_EMAIL` is raised, before any database exists), `TenantDatabase` (`Pending`).
2. `Tenant.MarkProvisioning()`.
3. `CREATE DATABASE [Negosio.<Slug>_<BranchCode>_<TenantIdPrefix8>]` — the name is
   **composed server-side**: `<Slug>` is the business name and `<BranchCode>` the initial branch code,
   each stripped to `[A-Za-z0-9]` and truncated (40 / 16 chars; fallbacks `Tenant` / `MAIN`), and
   `<TenantIdPrefix8>` is the first 8 uppercase hex of the tenant id — which guarantees uniqueness, so
   two identically-named businesses never collide. The final name is regex-validated
   (`^Negosio\.[A-Za-z0-9]{1,40}_[A-Za-z0-9]{1,16}_[A-F0-9]{8}$`), always bracket-quoted, and guarded
   by `IF DB_ID(...) IS NULL`. Raw user input never reaches the DDL string. The name is fixed at
   provisioning and is **not** changed if the business name or initial branch code is later edited.
4. `MigrateAsync()` on a context opened against the new database (idempotent).
5. **Tenant-database transaction:** upsert `TenantProfile`; `if (!Branches.Any())` create the first
   branch; `if (!Users.Any())` create the owner `User` (`Id` == `PlatformUserLogin.Id`).
6. `Tenant.MarkActive()` + `TenantDatabase.MarkActive()`.

Failure in steps 2–5 → `Tenant.MarkFailed()` + `TenantDatabase.MarkFailed()`, log the SQL detail
server-side only, throw `TenantProvisioningException` → `500 TENANT_PROVISIONING_FAILED` with a generic
message.

**Retry channel is a repeat `POST /api/auth/register` with the same email.** If a login already exists
for that email:
- status `Active` or `Suspended` → `409 DUPLICATE_EMAIL` (a real, finished account).
- status `Pending` / `Provisioning` / `Failed` → **resume** from step 2 without re-inserting platform
  rows; the original tenant id is kept. `CREATE DATABASE` is `IF DB_ID IS NULL`, `MigrateAsync` is
  idempotent, and every seed insert is `if (!Any())`, so resuming is safe from any interruption point.

## Consequences

- No partial tenant is *observable*: a half-provisioned tenant is `Failed`/`Provisioning`, and the
  connection resolver returns `403 TENANT_UNAVAILABLE` until it reaches `Active`.
- Registration stays a single idempotent call; no separate "finish setup" endpoint, no background
  worker, no orchestrator in 2.5.
- An orphaned physical database can exist after a failure (created in step 3, then step 4/5 failed).
  It is reused verbatim on retry; a janitor for genuinely abandoned `Failed` tenants is a later
  concern.
- The workflow is synchronous inside the register request — acceptable for a once-per-business action
  (observed ~300 ms locally including `CREATE DATABASE` + migrate). A queue is the Phase 6+ path.
