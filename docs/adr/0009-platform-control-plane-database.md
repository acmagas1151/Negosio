# ADR 0009 — Platform (control-plane) database

- Status: Accepted
- Date: 2026-08-29
- Phase: 2.5 (Database-per-Tenant Architecture)

## Context

With one database per tenant ([ADR 0008](0008-database-per-tenant.md)), something outside any tenant
database must answer two questions on every request: *which database does this tenant live in?* and, at
login, *which tenant does this email belong to and what is its password hash?* That data cannot live in
a tenant database (you need it before you can pick one) and must not be duplicated per tenant.

## Decision

A single fixed **platform database** (`Negosio_Platform`, connection string `ConnectionStrings:Platform`)
with its own `PlatformDbContext` and three tables:

| Table | Purpose |
| --- | --- |
| `Tenants` | Control-plane identity: `Name`, `BusinessType`, `IsActive`, `ProvisioningStatus`. No branches/users/tax — those moved to the tenant DB's `TenantProfile`. |
| `TenantDatabases` | 1:1 routing row: `TenantId` (unique), `DatabaseName`, `ServerKey`, `Status`. |
| `PlatformUserLogins` | The login directory: `Id` (== tenant `User.Id`), `TenantId`, `EmailNormalized` (**globally unique**), `PasswordHash`, `Role`, `IsActive`. |

**The password hash lives only here.** The tenant `User` row carries no hash; login never opens a
tenant database. `PlatformUserLogin.EmailNormalized` keeps Phase 1's global-unique-email semantics
(one email ⇒ one login ⇒ one tenant), and registration's `DUPLICATE_EMAIL` is raised by that index
*before any tenant database is created*.

Login flow: `PlatformUserLogins` lookup by email → timing-equalised hash verify → check
`Tenant.IsOperational` and `login.IsActive` → mint JWT (`sub`, `jti`, `email`, `tenant_id`, `role` —
identical claims to Phase 1) → open a short-lived `TenantDbContext` only to fill the user's name and
business type into the response.

The platform database never stores connection strings or credentials for tenant servers — those come
from configuration templates keyed by `ServerKey` ([ADR 0010](0010-tenant-connection-resolution.md)).

## Consequences

- Login is one fast control-plane query and works even while a tenant database is migrating or offline.
- Routing, suspension and "which tenants exist" are answered from one place; a support tool touches one
  small database.
- The platform database is a **always-on dependency for every authenticated request** (already true of
  any routing scheme) and a single point of failure — it must be backed up and monitored like the
  tenant data it points at.
- Password changes (future) write only `PlatformUserLogin`; the tenant `User` row is no longer
  self-sufficient for auth. Acceptable — it never was the boundary.
- Future per-tenant email reuse is a documented, non-breaking change: relax the unique index to
  `(EmailNormalized, TenantId)` and add a workspace selector to login. Not built now.
