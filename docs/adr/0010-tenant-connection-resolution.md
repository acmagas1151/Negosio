# ADR 0010 — Per-request tenant connection resolution

- Status: Accepted
- Date: 2026-08-29
- Phase: 2.5 (Database-per-Tenant Architecture)

## Context

Every authenticated request must run against the caller's tenant database. `TenantDbContext` is a
scoped service, so its connection string has to be known *before* any service resolves it, but *after*
authentication has produced a `tenant_id` claim. The connection string must never be influenced by
client input, and the lookup that produces it must not add a database round-trip to every request in
the steady state.

## Decision

A four-part pipeline, all wired in `Negosio.Infrastructure.DependencyInjection`:

1. **`ITenantContext` (`HttpTenantContext`)** — reads and parses the `tenant_id` claim from the
   validated JWT. `HasTenant` is false for anonymous requests (register, login, swagger).
2. **`TenantResolutionMiddleware`** — runs *after* `UseAuthentication`, *before* `UseAuthorization`.
   If `HasTenant`, it calls the resolver and stashes the result in a scoped `TenantConnectionAccessor`.
   Anonymous requests pass straight through. A thrown `AppException` is rendered by
   `ExceptionHandlingMiddleware` like any other.
3. **`ITenantConnectionResolver` (`TenantConnectionResolver`)** — looks up
   `(DatabaseName, ServerKey, ProvisioningStatus, IsActive)` by joining `Tenants` and `TenantDatabases`
   in the platform database, cached in `IMemoryCache` for **60 seconds**. It then:
   - unknown tenant → `404 TENANT_NOT_FOUND`
   - `!IsActive` or `Suspended` → `403 TENANT_SUSPENDED`
   - any non-`Active` provisioning status → `403 TENANT_UNAVAILABLE`
   - otherwise composes the connection string from the `ServerKey` template + `DatabaseName`.
4. **`TenantConnectionAccessor.Require()`** — what the `AddDbContext<TenantDbContext>` factory calls to
   get the connection string; throws `TENANT_UNAVAILABLE` if nothing was resolved (a tenant-scoped
   endpoint reached without a tenant).

**Connection strings are composed server-side only.** `TenantServerRegistry` reads a per-server
template (`TenantDatabases:Servers:<key>` — server + auth, no database) from configuration and sets
`Initial Catalog` on it via `SqlConnectionStringBuilder` (which quotes the dot in a tenant DB name
such as `Negosio.Acme_MAIN_1A2B3C4D`). The client supplies neither half. Server credentials are read
in exactly one place; the Azure path evolves this toward Managed Identity / Key Vault.

The cache stores only `(DatabaseName, ServerKey, status)` — **never** a `DbContext`, an open
connection, or a secret. `ITenantDbContextFactory` builds short-lived contexts for provisioning and
tests off an explicit connection string, bypassing the request-scoped accessor.

## Consequences

- Steady-state requests pay one `IMemoryCache` hit, not a platform-database query.
- A suspension or a server move takes effect within the 60-second cache window — bounded and
  documented. Tests evict the key to exercise the path immediately.
- All four "tenant not usable" outcomes are decided before any handler runs and before a tenant
  connection is opened.
- `IMemoryCache` is per-process; a multi-instance deployment resolves independently (fine — the data
  is a tiny, self-healing projection). A distributed cache is explicitly out of scope for 2.5.
