# RestoPOS M3 — Reliability and Reconciliation Plan (revised)

> **Status:** Revised after review. Ready for implementation approval. Nothing implemented. Not committed.
> **Spec:** `docs/superpowers/specs/2026-09-28-restopos-design.md` — Sections 6.2 (kitchen statuses), 6.3 (concurrency), 6.4 (PAYO release contract), milestone M3 (line 446).
> **Depends on:** M2 (`1020a3c`, on `master` and `origin/master`).

## 0. Environment and migration results (verified)

- **Git:** `master` == `origin/master` == `1020a3c`. Only `handover.md` is modified. No M3 code exists yet.
- **Migration process:** the API runs `ApplyMigrationsAsync` on startup when `Database:MigrateOnStartup` is true (Development: true). It migrates `Negosio_Platform`, then every row in `TenantDatabases`. `TenantProvisioningService` also migrates new tenant databases.
- **Targets:** `Negosio_Platform` on `(localdb)\MSSQLLocalDB` maps 40 tenant databases, all server key `default`. Verified: `20261006045817_AddRestoM2Lifecycle` is applied to 40/40 (independent check). Platform DB had no pending migrations.
- **Unmapped:** 14 `Negosio.BrunosCafe_*` databases exist but are not mapped, so startup never touches them. They remain untouched.
- **Not applied:** staging and production (none configured here).

## 1. Scope

In M3:
1. A background worker that releases settled PAYO rounds to the kitchen, tenant by tenant, with failure isolation and no batch starvation.
2. One shared release operation used by both the worker and the manual action.
3. A manual release action with tenant and branch checks.
4. Read-only endpoints for pending PAYO releases and for kitchen tickets that are available but unacknowledged.
5. Configuration, cancellation, worker identity, and logging.
6. Tests for restart recovery, tenant isolation, failed releases, duplicate attempts, manual/automatic races, and sale eligibility.

Not in M3: KDS screens and acknowledgement endpoints (M5), frontend (M7), push or email alerts, permission hardening (M4), reports (M6).

## 2. Decisions (approved and revised)

| # | Decision | Final |
|---|---|---|
| D1 | Manual release authorization | `PosOperate`, plus tenant check and branch check (non-admin roles limited to their assigned branch). |
| D2 | Automatic release delay | **Grace 30 seconds** after settlement; **poll every 15 seconds** (allowed range 10–15 s; default 15). Harmless races are already idempotent. |
| D3 | Alerts | Read endpoints and structured logs in M3. UI in M7. |
| D4 | Worker host | `BackgroundService` inside the API host. |

## 3. API contracts

| Contract | Shape | Notes |
|---|---|---|
| Manual release | `POST /api/resto/orders/{id}/rounds/{roundId}/release` | **Exists.** Keeps `RestoStructuralRequest` (RowVersion). Now delegates to the shared release operation (§4.1). |
| Pending PAYO releases | `GET /api/resto/orders/pending-releases?branchId=&limit=&afterOrderId=` | **New.** Settled PAYO orders whose single round is `Draft` with a non-voided item, oldest first. |
| Available but unacknowledged | `GET /api/resto/orders/unacknowledged-tickets?branchId=&limit=&afterItemId=` | **New.** Items on a `Released` round with `KitchenStatus = Pending`, non-voided. |

Rules for both list endpoints:
- Authorized with `PosOperate`; results are restricted to the caller's assigned branch when one exists, and to the tenant always.
- **Bounded:** `limit` defaults to 50, maximum 200. Keyset paging via the `after*` parameter. The response includes `nextAfterId` when more rows exist.
- **No time filter from the client.** The server applies the thresholds. Each row returns `ageSeconds`, so the UI can display age itself.
- **Pending-releases** does not apply the grace period: it lists every pending round, so a manual operator can see what is waiting.
- **Unacknowledged-tickets** applies `UnacknowledgedAlertMinutes` server-side: `ReleasedAtUtc <= now - UnacknowledgedAlertMinutes`. The threshold is configuration, not a request parameter, so the endpoint and the log alert always agree.

Precise wording used everywhere: `Released` + `Pending` means **available to the kitchen but not acknowledged**. It does not prove the kitchen received or displayed the ticket. Response field names reflect this (`availableUnacknowledged`, not `received`).

**Deferred:** KDS acknowledge/ready/served endpoints (M5); push or email alerts (D3).

## 4. Design

### 4.1 One release operation

A single internal operation, `PayoRoundReleaser.ReleaseAsync(db, tenantId, orderId, roundId, actor)`, performs every release. It is the only place that changes a round to `Released`.

- **Locking:** takes the register-session lock of the linked sale first, then the order lock (`RestoOrderLocking`, `UPDLOCK,HOLDLOCK`). This is the same order as settlement and void (spec 6.1), so release cannot deadlock with them.
- **Sale eligibility under the lock:** the linked `Sale` must exist and have `Status = Completed`. If it was voided, the release is refused and the round stays `Draft`. Reason: a paid sale voided before kitchen release must never reach the kitchen.
- **State checks under the lock:** order `Settled`, service type PAYO, round `Draft` (no-op if already `Released`), at least one non-voided item.
- **Transition:** `round.ReleaseBySystem(now)` for the worker, `round.Release(userId, now)` for the manual action. Both call the existing idempotent `ReleaseCore`.

Callers wrap the core differently:
- **Manual action** (`RestoOrderService.ReleaseRoundAsync`): validates the client RowVersion after the lock, applies the branch guard, and returns the projected order. `PosOperate` applies at the endpoint.
- **Automatic worker** (`PayoReleaseWorker`): no RowVersion, no client; branch is not scoped (the worker serves the whole tenant); it records no user actor.

No second implementation exists. Both paths call the same core, so the sale and state rules cannot drift.

### 4.2 Tenant-scoped access without an HTTP user

The current `ListPendingPayoReleasesAsync` and `ReleaseRoundBySystemAsync` read the tenant and branch from `ICurrentUser`, which a worker does not have. New code takes `tenantId` explicitly:
- `IPayoReleaseQueries.ListPendingAsync(tenantId, branchId?, afterOrderId?, limit)`
- `IPayoReleaseQueries.ListUnacknowledgedAsync(tenantId, branchId?, afterItemId?, limit, unacknowledgedBeforeUtc)`
- `IPayoReleaseWorkerService.ReleaseDueRoundsAsync(tenantId, graceBeforeUtc, batchSize, ct)` returning counts (`Released`, `AlreadyReleased`, `Refused`, `Failed`) and the failed order ids.

`RestoOrderLocking.LockAndLoadAsync` gets an overload that takes an explicit branch scope (`null` = whole tenant), so branch isolation for manual calls is unchanged.

### 4.3 Worker

- `PayoReleaseWorker : BackgroundService` in the API host (D4).
- Each cycle: enumerate tenants with `TenantDatabaseStatus.Active` from `Negosio_Platform.TenantDatabases`; for each, open a tenant context via `ITenantDbContextFactory.CreateAsync(tenantId)` and run `ReleaseDueRoundsAsync`.
- **Tenant isolation:** each tenant has its own try/catch and timeout. A tenant that throws (database unreachable, bad row) is logged with its tenant id and placed in backoff: 1, 2, 4 … minutes, capped at `TenantBackoffMaxMinutes` (default 10). Other tenants proceed in the same cycle.
- **No batch starvation:** a repeatedly failing order must not take the head of every batch. Two mechanisms:
  1. The batch is read with **keyset paging** (`SettledAtUtc, OrderId`). Within one cycle the worker advances the cursor past any failed order, so later orders in the same cycle are still reached.
  2. Each failed order gets an **in-memory backoff** (per order id, exponential, capped at 10 minutes). Backed-off orders are skipped on later cycles. After a restart the backoff resets, so a poison order gets one new attempt per restart. That is acceptable and is noted as a limitation.
- **Per-order transactions:** each order release commits or rolls back on its own. A failure does not undo other orders in the batch.
- **Cancellation:** honors `stoppingToken` between tenants and orders. In-flight transactions roll back through EF transaction disposal, so no round is left partly released.
- **Identity:** `{MachineName}:{ProcessId}:{shortGuid}`, created at startup and included in every log line. Released rounds keep `ReleasedByUserId = null`.
- **Configuration** (`Resto:Reconciliation`, validated at startup):
  - `Enabled` — default `true`.
  - `PollIntervalSeconds` — default `15`, allowed 10–15.
  - `ReleaseGraceSeconds` — default `30`, allowed 0–300.
  - `BatchSize` — default `50` per tenant per cycle, allowed 1–500.
  - `UnacknowledgedAlertMinutes` — default `5`, allowed 1–120.
  - `TenantBackoffMaxMinutes` — default `10`.
- **Restart recovery:** no in-memory queue. The source of truth is the database (settled PAYO order with a `Draft` round and a non-voided item).
- **Logging:** one line per tenant per cycle when anything happened. Warning level for failures and for unacknowledged tickets over threshold. Each line includes tenant id, worker id, and counts. No sale or customer data in logs.

### 4.4 Races

| Race | Outcome | Why it's safe |
|---|---|---|
| Two worker instances, same round | One `Released`; the other is `AlreadyReleased` | Order lock; `ReleaseCore` is a no-op on `Released`. |
| Worker and manual release, manual submitted with fresh RowVersion | Either order of arrival is safe. If the worker commits first, the manual request gets **409 `RestoOrderConcurrencyConflict`** because its RowVersion is now stale. If manual commits first, the worker sees `Released` and no-ops. | Serialized by the order lock. The 409 is correct even though the RowVersion was fresh when the request was sent: it went stale while waiting for the lock. |
| Manual with stale RowVersion | 409 | Client refreshes. |
| Void of the sale while worker is between lock and commit | The worker takes the session lock first; void takes the session lock too, so they serialize. If void wins, release sees a voided sale and is refused. | Lock order matches settlement and void. |
| Sale voided after release | Not reversed. The kitchen already has the ticket; void follows the existing item/void rules. | Out of scope; release is one-way. |

### 4.5 Alerts (query-based, no new table, no migration)

- **Pending release:** PAYO, settled, round `Draft`. Surfaced by `pending-releases`. Released by the worker after the grace period, or manually.
- **Available but unacknowledged:** item `KitchenStatus = Pending` on a `Released` round, released longer than `UnacknowledgedAlertMinutes`. Surfaced by `unacknowledged-tickets` and by one warning log line per tenant per cycle when non-zero.

Relation between the threshold and `olderThanUtc`: the server computes `olderThanUtc = now − UnacknowledgedAlertMinutes` at request time. Clients never send it. A ticket appears in the alert exactly when it has been available for at least the threshold.

## 5. Tasks (implementation, after approval)

1. `RestoReconciliationOptions` with validation (ranges in §4.3).
2. `PayoRoundReleaser` shared core (§4.1); manual `ReleaseRoundAsync` refactored to use it; existing M2 tests must still pass.
3. `LockAndLoadAsync` explicit-branch-scope overload.
4. Tenant-explicit queries for pending and unacknowledged lists (keyset paging, limits).
5. `IPayoReleaseWorkerService` (per-tenant, per-order, backoff, counts).
6. `PayoReleaseWorker` `BackgroundService`: cycles, tenant backoff, cancellation, identity, logging; registered only when `Enabled`.
7. Endpoints `GET pending-releases` and `GET unacknowledged-tickets` in `RestoOrdersController` (`PosOperate`).
8. Unit tests for options validation, counting, and backoff; integration tests (§6).
9. Run unit and integration suites after each task. Update handover and status. No commit.

## 6. Tests

- **Restart recovery:** settle a PAYO order without releasing; run the worker once → `Released`. Create a new worker instance and run again → no-op; `ReleasedByUserId` stays null.
- **Duplicate attempts:** N concurrent releases of the same round (separate scopes) → exactly one `Released` transition; items enter `Pending` once; `ReleasedAtUtc` is unchanged after the first commit.
- **Manual vs automatic race:**
  - (a) Worker and manual submitted together → the result is either manual success with the worker as a no-op, or a 409 for the manual call. **Both are valid.** The assertion is: one released round, no double transition, and no error other than the clean 409.
  - (b) Worker commits first, then a manual request with the pre-release RowVersion → 409 `RestoOrderConcurrencyConflict`.
- **Sale eligibility:** a PAYO sale voided before the worker runs → the round stays `Draft`, the worker reports `Refused`, and the round is never released.
- **Tenant isolation:** two tenants with pending PAYO orders; a worker cycle for tenant A releases only A's rounds; B's stay `Draft`. `pending-releases` as tenant B never returns A's rows.
- **Failed release:** an unreachable tenant is logged, backed off, and does not block the next tenant (unit test with fake tenant source; integration test with a bad connection string for one tenant). A failing order does not block later orders in the same cycle.
- **Starvation:** a batch whose first order always fails still releases the orders behind it in the same cycle.
- **Cancellation:** cancelling mid-batch leaves the interrupted round `Draft`; the next cycle releases it.
- **Alerts:** an item released longer than the threshold appears; an acknowledged item does not; a voided item does not; an item under the threshold does not; a pending-release PAYO round (still `Draft`) does not appear in the unacknowledged list.
- **Paging:** `limit` is enforced (cap 200); `nextAfterId` is returned when more rows exist; paging through all rows returns each exactly once.
- **Branch scope:** a branch-assigned cashier sees only their branch's rows and cannot release another branch's round (404, not 403, matching existing branch isolation).
- **Config:** out-of-range values fail startup validation.

## 7. Open risks

- Multiple API instances each run the worker. Correctness is guaranteed by the locks and idempotency. Extra database load grows with instance count; mitigated by `BatchSize` and `PollIntervalSeconds`.
- In-memory per-order backoff resets on restart, so a poison order gets a fresh attempt after each restart. Acceptable; a persistent attempt counter would need a column and migration, which is out of scope unless you want it.
- The lock order (session, then order) must hold in the release path. A regression test covers it indirectly through the void race; a direct deadlock test is hard to make deterministic and is not planned.
- The 14 unmapped `BrunosCafe` databases are orphans. They are not in the platform mapping and will not be polled or migrated. Deleting them needs your confirmation.

## 8. Ready for approval

Approve this revision and I'll implement tasks 1–9 in order, running the unit and integration suites after each task. Nothing is committed, merged, or pushed without your instruction.
