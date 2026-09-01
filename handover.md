# Handover Summary

> Session date: 2026-09-01 · Supersedes the "POS frontend vertical" handover.
> **Active branch: `feature/staff-management`** — Phase 4 (Staff & Access Management),
> built and browser-verified this session. **9 commits ahead of `master`. NOT merged —
> waiting for your review.**
> `master` now includes the merged `feature/retail-polish` (dashboard metrics + tax
> settings page) from the previous session and is pushed to `origin`.

---

## Project Context

- **Project:** **Negosio** — a multi-tenant SaaS platform for retail and food & beverage
  businesses. Database-per-tenant. Owner signs up → picks a business type → describes a
  first branch → creates an owner account → lands in a tenant-scoped dashboard. Commercial
  surface: product catalog, inventory, retail POS (registers, sessions, sales, payments,
  returns), and now **staff & access management**.
- **Tech stack:**
  - **Backend:** C# / .NET 9, ASP.NET Core Web API, EF Core 9, SQL Server (LocalDB dev,
    Testcontainers/LocalDB tests). JWT bearer auth, ASP.NET Core `PasswordHasher` (PBKDF2),
    FluentValidation, Serilog. Modular monolith: `Api → Infrastructure → Application → Domain`.
  - **Frontend:** React 19, TypeScript (strict), Vite 8, React Router 7, TanStack Query v5,
    Tailwind v4, lucide-react. Lint = `oxlint`. Path: `web/negosio-web/`. No frontend test
    runner — verified via `npm run lint` + `npm run build` + headless-Chrome/CDP walkthroughs.
  - **Tests:** xUnit, FluentAssertions, `WebApplicationFactory`.
- **Roadmap:** Phase 1 (SaaS foundation) ✅ · Phase 2 (Catalog + Inventory) ✅ · Phase 2.5
  (Database-per-Tenant) ✅ · Phase 3 (Retail POS) ✅ · Phase 3.1 (retail polish — dashboard
  metrics + tax settings) ✅ merged · **Phase 4 (Staff & Access Management) — this session,
  on `feature/staff-management`, unmerged** · Phase 5+ future.

---

# Phase 4 Final Report

## 1. Architecture

**Goal:** make Negosio usable by a real business team by *activating* the RBAC model that
already existed — not redesigning it.

- **Identity model — unchanged and deliberately single-tenant.** One email = one platform
  login = one tenant. Authentication credentials stay in `Negosio_Platform`
  (`PlatformUserLogin`), which remains the sole auth authority. A staff member has exactly
  **one** role, drawn from the existing `UserRole` enum, mapped to the existing
  `AuthorizationPolicies`. No custom roles, no permission matrices, no multiple roles, no
  cross-tenant memberships. A future `PlatformUser` / `TenantMembership` split is explicitly
  *not* this task.
- **The app user is the POS cashier.** No separate POS-PIN identity, no cashier switching.
- **Invitations live in the platform DB.** `StaffInvitation` is a platform entity because
  invitation acceptance happens **pre-auth** (the token, not a JWT, resolves the tenant) and
  because the "one email globally" check spans all tenants.
- **Cross-DB write pattern (mirrors `TenantProvisioningService`).** On acceptance the
  platform `PlatformUserLogin` row is written first (it is the source of truth for "this
  account is real"), then the tenant `User` row. A recovery branch re-runs the tenant-row
  step if the process died between the two writes. No distributed transaction.
- **Staff list is a merged read across both DBs** (tenant `Users` for names + platform
  `PlatformUserLogins` for the authoritative role/active flag + open platform
  `StaffInvitations`). It is a flat, non-paged array — a deliberate simplification for a
  bounded admin dataset.

## 2. Backend

**New / changed files** (all on `feature/staff-management`):

- **Domain:** `Entities/Platform/StaffInvitation.cs` (+ `StaffInvitationStatus`); `User.cs`
  gains `ChangeRole` / `Deactivate` / `Reactivate` with Owner guards; `PlatformUserLogin.cs`
  gains `ChangeRole` / `Reactivate`.
- **Application:** `Staff/` (`StaffContracts`, `StaffValidators`, `StaffRoles`,
  `StaffService`, `StaffInvitationService`); `Common/PasswordRules.cs` (extracted from the
  registration validator — one shared FluentValidation `.Password()` rule);
  `Common/InvitationToken.cs` (32 random bytes → base64url raw, SHA-256 hex hash stored);
  `Common/ErrorCodes.cs` Phase 4 codes; `ForbiddenAppException`; `Abstractions/IAppEnvironment.cs`.
- **Infrastructure:** `StaffInvitationConfiguration` + migration
  `20260831163220_AddStaffInvitations` (`PlatformDbContext`). Unique index on `TokenHash`;
  filtered unique index on `(TenantId, EmailNormalized)` while pending.
- **API:** `Controllers/StaffController.cs` (`[Authorize(Policy = StaffManage)]`, `api/staff`
  — list / get / invite / resend / revoke / role / deactivate / reactivate);
  `AuthController` gains `[AllowAnonymous]` `GET`/`POST api/auth/invitations/{token}[/accept]`;
  `AuthorizationPolicies.StaffManage` (Owner + Admin); `Authentication/HostAppEnvironment.cs`;
  `Authentication/ConfigureJwtBearerOptions.cs` gains `OnTokenValidated`.

**Domain errors, not 500s:** `STAFF_SELF_ACTION`, `OWNER_PROTECTED`, `OWNER_ROLE_FORBIDDEN`,
`ROLE_NOT_ASSIGNABLE`, `LAST_OWNER`, `STAFF_EMAIL_IN_USE`, `STAFF_ALREADY_INVITED`,
`INVITATION_INVALID`, `INVITATION_NOT_FOUND`, `INVITATION_ALREADY_ACCEPTED`,
`STAFF_ALREADY_ACTIVE`, `STAFF_ALREADY_DEACTIVATED` — all surfaced through the existing
`ExceptionHandlingMiddleware` with their HTTP status.

## 3. Frontend

**New / changed files** (all under `web/negosio-web/src/`):

- **API/lib:** `api/staff.ts` (`staffApi` + public `invitationsApi`), Phase 4 section in
  `api/types.ts`, `lib/roles.ts` (labels + `assignableRoles` mirroring `StaffRoles`),
  `lib/useCan.ts` gains `staff:manage` + a `useCapabilities()` predicate hook,
  `lib/nav.ts` fully rewritten — `NavItem.capability`, and the old `TODO(staff-management)`
  is **resolved**.
- **Guards / layout:** `auth/RequireCapability.tsx` (page-level guard → clean "You don't
  have access to this page" panel, never raw JSON); `Sidebar.tsx` filters nav groups by
  capability and hides empty groups.
- **Staff UI:** `pages/StaffPage.tsx` (search + role/status filters, invitation rows vs
  member rows, self / Owner-row action hiding), `components/staff/` (`InviteStaffModal`,
  `ChangeRoleModal`, `StaffBadges`).
- **Public invite acceptance:** `pages/InviteAcceptPage.tsx` (`/invite/:token` — preview,
  set name + password, redirect to `/login` prefilled). `App.tsx` adds the public route and
  wraps `/staff` in `RequireCapability`.

**Capability-aware navigation:** Staff and Settings appear only for Owner/Admin; POS /
Registers / Sales gate on `pos:operate` / `sales:view`; Catalog / Inventory / Dashboard
stay ungated (their backends are read-open and their write controls self-gate).

## 4. Security

- **Invitation tokens:** 32 cryptographically-random bytes, base64url. Only the SHA-256 hex
  **hash** is stored. Single-use (`AcceptedAtUtc`), expiring (7 days), tenant-bound,
  email-bound. Invalid immediately after accept or revoke. Raw token is returned in the API
  response and structured log **only outside Production** (`IAppEnvironment.IsProduction`);
  in Production `acceptPath` is `null`. Email delivery is the documented next integration
  step — no provider is wired in.
- **JWT freshness / revocation:** the role is a `role` claim in a 60-minute JWT.
  `OnTokenValidated` runs one indexed `PlatformUserLogins` lookup on **every authenticated
  request** and calls `context.Fail(...)` if the login is missing, inactive, or its role no
  longer matches the token's `role` claim. Net effect: **deactivation takes effect within
  one request**; a **role change forces re-login**. No blacklist, no Redis, no shortened
  lifetime.
- **Owner protection:** Admin cannot create/promote to Owner, cannot change or deactivate an
  Owner. Nobody can deactivate or re-role themselves. The last active Owner cannot be
  removed (`LAST_OWNER`). No ownership transfer.
- **Manager gets no staff administration** — `StaffManage` is Owner + Admin only.
- **Passwords:** the exact registration rules, now the shared `PasswordRules.Password()`
  FluentValidation extension. Hashed with the existing `IPasswordHasher`. No plaintext.
- **Defence in depth:** navigation hiding is cosmetic; every route's backend policy still
  returns 403/401, and `RequireCapability` renders a clean unauthorized panel for a manually
  typed URL.
- **History preserved:** deactivation never deletes. `Sale.CreatedByUserId` /
  session / return references survive; no cascade.

## 5. Tests

`dotnet test Negosio.sln` → **76 unit + 114 integration, 0 failed** (unit ~0.2 s,
integration ~6m47s, shared `(localdb)\MSSQLLocalDB`).

- **New unit** — `tests/Negosio.UnitTests/Staff/StaffDomainTests.cs`: `StaffInvitation`
  lifecycle (create rejects Owner, `StatusAt`, `Accept`/`Revoke`/`Reissue` guards),
  `User` / `PlatformUserLogin` role-change + Owner guards, `StaffRoles.AssignableBy`
  (Owner → all non-Owner; Admin → non-Owner minus Admin; others → none), `PasswordRules`
  (four character classes + length as a separate rule), `InvitationToken` (raw ≠ hash,
  stable hash, unique).
- **New integration** — `tests/Negosio.IntegrationTests/Staff/StaffTests.cs` (13):
  Owner invites Cashier who accepts and logs in · token is single-use (409
  `INVITATION_ALREADY_ACCEPTED`) · revoked invitation cannot be previewed/accepted ·
  unknown token → clean 404 · an email that already has an account cannot be invited into
  another tenant (409 `STAFF_EMAIL_IN_USE`) · Owner and Admin may list staff, others 403 ·
  Admin never creates an Admin/Owner (`ROLE_NOT_ASSIGNABLE` / `OWNER_ROLE_FORBIDDEN`) ·
  Admin cannot change or deactivate the Owner (`OWNER_PROTECTED`) · Owner cannot
  self-deactivate / self-re-role (`STAFF_SELF_ACTION`) · role change takes effect only after
  re-login (old token → 401) · deactivation stops access immediately (old token → 401,
  re-login → `ACCOUNT_INACTIVE`) and preserves `Sale.CreatedByUserId`; reactivation
  restores · deactivated Cashier cannot obtain cost prices · one tenant cannot see or touch
  another tenant's staff.
- **Changed** — `IntegrationTest.AddTenantUserTokenAsync` now also seeds a
  `PlatformUserLogin` (so `OnTokenValidated` passes); `ResetDatabaseAsync` clears
  `StaffInvitations`. `TenantRoutingTests`: the old "unknown tenant → 404" test split into
  **"a token for a non-existent user → 401"** (the new per-request check rejects it before
  routing — this is more correct) and a new **"valid login, unroutable tenant → 404
  `TENANT_NOT_FOUND`"** that seeds a login so the 404 path stays covered.

`npm run lint` (oxlint) + `npm run build` (`tsc -b && vite build`) → **clean**. Bundle
≈ 459.6 kB js / 129.4 kB gz.

## 6. Browser Verification

Headless Chrome + CDP against the real API (`ASPNETCORE_ENVIRONMENT=Development`) and
`npm run dev`. Script: this session's scratchpad `verify-staff.mjs`; screenshots `90-…`–`96-…`.
**Zero console errors on every screen.** Scenarios actually executed:

1. Owner invites a Cashier via the API → `201`, `acceptPath` returned (dev), staff list
   shows the pending `Invited` row.
2. `/invite/:token` in the browser → preview shows business name, invited role and the
   locked email → set first/last name + password → **redirected to `/login`** with a "Your
   business is ready" callout and the email prefilled.
3. Re-previewing the used token → `400 INVITATION_INVALID` (single-use confirmed).
4. New Cashier logs in → lands on `/dashboard`; sidebar shows Dashboard / Products /
   Categories / Stock levels / Stock movements / POS / Registers / Sales — **no Staff, no
   Settings**.
5. Cashier manually navigates to `/staff` → clean "You don't have access to this page"
   panel (no raw JSON). To `/settings` → read-only notice, Save hidden.
6. Cost redaction: Cashier `GET /api/products/{id}` → every variant `costPrice: null`;
   Owner sees real cost.
7. Owner changes the Cashier → Manager. The **old Cashier token → 401** on the next call;
   re-login → role `Manager`, can `GET /api/sales` (`200`), still `403` on `/api/staff`;
   sidebar still has no Staff.
8. Owner deactivates the member. The **existing Manager token → 401** immediately; a fresh
   login → `400 ACCOUNT_INACTIVE`; the member still appears in the list as `Deactivated`
   with a Reactivate action.
9. Owner reactivates → the member can log in again, role still `Manager`.
10. Owner cannot self-deactivate (`403 STAFF_SELF_ACTION`); cannot invite an Owner
    (`403`).
11. RBAC regression sweep — Owner across all nine dashboard routes, Cashier across the two
    gated routes: correct render, no console errors, no raw error JSON.

**Not exercised in-browser** (covered by integration tests): the Admin-role limits from an
actual Admin browser session; `LAST_OWNER`; invitation resend from the UI button. The
Change-role and Invite modals were driven via their API endpoints rather than clicked.

## 7. Git State

- **`feature/staff-management`** — 9 commits ahead of `master`, working tree clean:

  | | |
  |---|---|
  | `feb870b` | feat(staff): invitation domain + platform persistence + migration |
  | `b0e3da5` | feat(staff): staff management + invitation application services |
  | `67e8db1` | feat(auth): enforce active user + fresh role on every authenticated request |
  | `422ebef` | feat(staff): staff & invitation API endpoints |
  | `33cfe51` | test(infra): AddTenantUserTokenAsync seeds a platform login; reset clears StaffInvitations |
  | `c314f4a` | test(staff): unit + integration coverage; invite link exposed outside Production |
  | `192f41c` | feat(staff): staff management + invite-acceptance frontend; capability-gated nav |
  | `355170e` | test(routing): a token for a non-existent user is now rejected at auth (401) |
  | `5ba64cd` | fix(web): keep the invite password hint to one line |

- **`master`** — includes the merged `feature/retail-polish`; pushed to `origin`; unchanged
  this session.
- **NOT merged.** Awaiting review. Do not merge without the user's word.

## 8. Remaining Limitations

- **One email = one tenant.** A staff email can belong to only one Negosio tenant. No
  cross-tenant memberships (a future `PlatformUser` / `TenantMembership` split).
- **No ownership transfer.**
- **No separate cashier PIN** — the app user *is* the POS cashier.
- **No Branch Management** (no add-branch, no branch assignments, no inventory transfers).
- **No Void, no Reporting.**
- **No offline selling.**
- **No F&B.**
- **No tenant timezone** — server UTC throughout.
- **PHP currency is fixed.**
- **No production email provider** — invitations are not emailed. Outside Production the
  accept URL is in the API response and the structured log; in Production it is withheld and
  a real provider (SendGrid / SES / Azure — none chosen) is the next integration step.

---

## Working dev login & data

- **Owner:** `invui@example.com` / `SecurePassword123!` — tenant "Inv UI Test", one branch
  "Main Branch" / MAIN, products Widget A + Widget B, a register, and prior POS sales.
- Verification this session left a throwaway staff account
  (`staffv+<timestamp>@example.com`, "Casey Register") in the tenant, currently
  **Deactivated**, plus a couple of spent/pending `staffv+…` / `reshot+…` invitations.
  Harmless.

## Prompt for Next Claude Session

```
You are continuing work on Negosio — a multi-tenant .NET 9 / EF Core / SQL Server + React 19
SaaS platform (modular monolith, database-per-tenant) for retail & F&B. Read handover.md
(repo root) first.

STATE (2026-09-01):
  - feature/staff-management has PHASE 4 (Staff & Access Management): StaffInvitation domain
    + platform migration, StaffService / StaffInvitationService, api/staff endpoints,
    OnTokenValidated per-request active+role check, StaffPage + InviteAcceptPage +
    capability-gated nav. 9 commits ahead of master, NOT merged, awaiting review.
  - Verified: dotnet test 76 unit + 114 integration green · npm lint+build clean ·
    headless-Chrome walkthrough of invite → accept → login → RBAC → role change → deactivate
    → reactivate, zero console errors.
  - master has the merged retail-polish (dashboard metrics + tax settings), pushed to origin.

DO NOW:
  1. Ask the user whether to (a) review + merge feature/staff-management, or (b) start
     Phase 5. Do NOT begin Phase 5 automatically. Do NOT merge without their word.
  2. If reviewing: superpowers:requesting-code-review, then
     superpowers:finishing-a-development-branch.

GUARDRAILS — do not undo:
  - NO "Co-Authored-By: Claude" / "Generated with Claude Code" trailer on commits.
    Commit on feature branches; merge only on the user's word.
  - One email = one platform login = one tenant. Do NOT redesign into cross-tenant
    memberships. Auth credentials stay in Negosio_Platform.
  - The app user IS the POS cashier — no separate PIN identity.
  - Do NOT recreate/rename roles or the policy matrix without a confirmed defect.
  - Invitation raw token: never in a Production response; store only the SHA-256 hash.
  - Never compute authoritative money/stock client-side.
  - Run the API: ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api
    --launch-profile http
  - Kill any stale Negosio.Api / vite process before building or dotnet test.

If something is uncertain, verify against the repo — it is the source of truth.
```

### Useful commands

```bash
dotnet test Negosio.sln                 # 76 unit + 114 integration (~7 min)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api --launch-profile http
cd web/negosio-web && npm run lint && npm run build
cd web/negosio-web && npm run dev       # http://localhost:5173
git checkout feature/staff-management
git log --oneline master..HEAD          # the 9 commits under review
```
