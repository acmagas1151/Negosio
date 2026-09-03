# Phase 6 — Void Sales & Cash Operations — Final Report

**Branch:** `feature/void-cash-operations` (27 commits ahead of `master`, not pushed, working tree clean)
**Status:** Implementation, task-scoped review, browser verification, and final whole-branch review all complete. **Not merged — awaiting explicit go-ahead.**

---

## 1. Architecture

**Void vs Return.** Void is a first-class status transition on the existing `Sale` aggregate (`Completed → Voided`), never modeled as "create a Return." It keeps the original `SaleNumber`, is full-sale only (no line-level/partial void), and is mutually exclusive with Return: a sale with any return cannot be voided, and a voided sale cannot be returned against — enforced on both the void and the return path, not just by hiding UI.

**Void eligibility**, checked in this exact order for every actor including Owner (authorization never overrides domain eligibility): `Status == Completed` → no Returns → `RegisterSession` still Open → `Sale.CompletedAtUtc` is the same UTC calendar day as `nowUtc`. The single source of truth is `VoidEligibility.Evaluate`, consumed identically by `VoidSaleService` (to reject) and `SaleQueryService` (to surface `canVoid`/`voidIneligibilityCode` to the frontend), so the button state and the backend's rejection can never drift apart. The session-open + same-day gate is what makes voiding a same-shift-only correction — once a session closes or a day rolls over, only Return remains available, and this is what let the whole phase avoid ever needing to touch a closed `RegisterSession`'s frozen reconciliation.

**Permission model.** Owner/Admin: direct void, any branch. Manager: direct void, own branch only. Cashier: direct void only with a per-user `SalesVoid` grant (`UserPermissionGrant` — row existence is the sole source of truth, no soft-revoke, unique per `(TenantId, UserId, Permission)`); without the grant, a one-time Manager/Admin/Owner password reverification is required. `VoidSaleService` explicitly allow-lists the four participating roles rather than inferring "not Owner/Admin/Manager ⇒ Cashier."

**Manager approval.** `ApproverVerificationService` reuses the exact password-verification path `AuthService.LoginAsync` uses (timing-equalized hash comparison for unknown emails, so response time doesn't leak account existence) but issues no JWT, caches nothing, and authenticates exactly one void request. It resolves the approver's tenant/role/branch and enforces Manager-same-branch / Owner-Admin-any-branch, collapsing every failure mode (unknown email, wrong password, wrong tenant, inactive account) into one generic `INVALID_APPROVER_CREDENTIALS`.

**Audit identity.** `VoidedByUserId` (who clicked Void) is always distinct from `ApprovedByUserId` (set only when the approval sub-flow ran) and never overwrites the original `CreatedByUserId`.

**Inventory reversal.** A new `StockMovementType.SaleVoid` (additive, `= 10`) and `IInventoryPosting.ReverseForVoidAsync` run inside the same DB transaction as the status change — a sale is never left Voided with inventory unrestored, or vice versa. Original `Sale`/`Payment`/`StockMovement` rows are never mutated; the void produces new, explicit reversal rows, preserving a complete ledger trace.

**Cash-accounting model.** `Expected = Opening + GrossCashSales − VoidedCashSales − RefundCashOut + CashIn − CashOut`. No new ledger table for void's cash effect — it's derived live from `Sale.Status == Voided` joined against the original `Payment` rows at reconciliation time, with Gross and Voided reported as separate breakdown lines (never a pre-subtracted number).

**Closed-session void — the design checkpoint the user locked explicitly mid-brainstorm.** Void requires an open session and same-UTC-day, full stop — no closed-session correction path of any kind was built. This is what let the phase avoid the much harder problem of retroactively correcting a frozen `RegisterSession`'s `ExpectedCash`/`ClosingCash`/`CashDifference`.

**Cash In/Out.** New `RegisterCashMovement` entity (append-only, `CashIn`/`CashOut`, amount always positive with direction from `Type`, never a signed amount), creatable only by the session's own owner on an Open session.

**Concurrency — the load-bearing architectural decision of this phase.** Two mechanisms, used consistently:
- **`Sale.RowVersion`** (SQL Server `rowversion`, EF `IsRowVersion()`) protects any two competing writes to the same Sale row — double-void and void-vs-return. `VoidSaleService` and `ReturnService` both catch the resulting `DbUpdateConcurrencyException`, roll back explicitly, and re-fetch fresh to report the sale's *accurate* current-state error rather than a generic conflict.
- **A pessimistic `SELECT 1 ... WITH (UPDLOCK, HOLDLOCK) WHERE Id = {sessionId}`** on the `RegisterSession` row, issued as the literal first statement inside a real explicit transaction, identically by all three writers that feed reconciliation: `VoidSaleService.VoidAsync`, `RegisterSessionService.ReconcileAndCloseAsync`, and `RegisterCashMovementService.CreateAsync`. `RowVersion` can't protect this cross-entity case (Void never writes to `RegisterSessions`), so the lock instead fully serializes whichever operation gets there first, forcing the other to wait until the first fully commits or rolls back before it can even begin its own reads. This is what closes void-vs-close and cash-movement-vs-close — both confirmed, not assumed, by concurrency tests proven (via calibrated head-start variants, added after an earlier false-green was caught) to actually exercise both sides of each race, not just be structurally capable of it.

**A design defect caught during the final review, now fixed:** `ReconcileAndCloseAsync` was the only one of the three lock-holders that took the lock but never re-verified session status afterward — two concurrent closes (realistic trigger: a force-close racing the session owner's own close) could both succeed, the second silently overwriting the first's reconciliation numbers. Fixed in the final fix wave with the same post-lock re-check pattern the other two services already had.

---

## 2. Backend

**New entities:** `UserPermissionGrant` (`TenantId, UserId, Permission, GrantedByUserId, GrantedAtUtc`, unique index), `RegisterCashMovement` (`TenantId, BranchId, RegisterSessionId, Type, Amount, Reason, CreatedByUserId, CreatedAtUtc`), `CashReconciliationBreakdown` (value object, not persisted separately — feeds `RegisterSession`'s five new columns).

**Changed entities:** `Sale` gained `VoidedByUserId`, `ApprovedByUserId`, `RowVersion` (reused the already-reserved `VoidedAtUtc`/`VoidReason` columns from the tenant baseline migration) and a new `Void(Guid voidedByUserId, string reason, Guid? approvedByUserId, DateTime nowUtc)` domain method — `nowUtc` is a required parameter, never `DateTime.UtcNow` read internally, so the same-day check and the audit timestamp can never disagree. `RegisterSession` gained `GrossCashSales/VoidedCashSales/RefundCashOut/CashIn/CashOut` (all nullable — historic pre-Phase-6 sessions render "not recorded" rather than a misleading ₱0.00) and its `Close(...)` signature grew a `CashReconciliationBreakdown` parameter.

**Migrations (4, all tenant-DB, all purely additive, all safely reversible):** `AddSaleVoidAudit`, `AddUserPermissionGrants`, `AddRegisterCashMovements`, `AddRegisterSessionCashBreakdown`.

**New services:** `SalesVoidPermissionService` (grant/revoke, Owner/Admin any Cashier, Manager own-branch-Cashier-only, no self-grant), `RegisterCashMovementService`, `ApproverVerificationService`, `VoidSaleService`, plus the shared static `VoidEligibility` helper.

**New endpoints:** `POST /api/sales/{id}/void`, `POST`/`GET /api/register-sessions/{id}/cash-movements`, `PUT /api/staff/{id}/permissions`.

**Policy changes:** `GET /api/staff` and `GET /api/staff/{id}` moved from `StaffManage` (Owner/Admin) to a new `StaffView` (Owner/Admin/Manager, service-filtered strictly to the Manager's own branch — never `BranchId == assigned || BranchId == null`, so a Manager can never see Owner/Admin rows). `PUT /api/staff/{id}/permissions` uses a new `StaffPermissionManage` policy (same three roles). Every other staff write endpoint stays `StaffManage`-only, unchanged. This surfaced a genuine ASP.NET Core gotcha mid-phase: class-level and method-level `[Authorize]` are ANDed, not overridden — `StaffController`'s class-level attribute was removed and every action given an explicit policy instead.

**New error codes:** `SALE_NOT_VOIDABLE`, `SALE_HAS_RETURNS`, `VOID_SESSION_CLOSED`, `VOID_CUTOFF_EXPIRED`, `VOID_APPROVAL_REQUIRED`, `INVALID_APPROVER_CREDENTIALS`, `VOID_APPROVER_NOT_AUTHORIZED`, `VOID_APPROVER_WRONG_BRANCH`, `SALES_VOID_SELF_GRANT`, `SALES_VOID_GRANT_ROLE_INVALID`, `CASH_MOVEMENT_INVALID_AMOUNT` (reserved, unused — the enum-validity failure path uses the generic `VALIDATION_FAILED` instead), `CASH_MOVEMENT_SESSION_CLOSED`, `CASH_MOVEMENT_NOT_OWNER`.

**Stock-movement change:** `StockMovementType.SaleVoid = 10` appended (1-9 never renumbered); `InventoryPosting.RestockForReturnAsync`/`ReverseForVoidAsync` were refactored onto one shared private helper (DRY, they differed only in movement type and reference id).

**Late-wave hardening (final review's fix wave):** `RegisterCashMovementValidator`/`RegisterCashMovement.Create` now reject an undefined `CashMovementType` at both the validator and domain-factory layer (an out-of-range integer previously deserialized successfully and silently vanished from reconciliation sums while still existing as a real movement). `StaffService.ChangeRoleAsync`/`ChangeBranchAsync` now delete a user's `SalesVoid` grant on any role change away from Cashier or any branch change (including a branch change reached through the role-change endpoint while the role stays Cashier) — closing a real gap where a demoted-then-repromoted Cashier could silently regain void authority, or a branch-moved Cashier could carry authority a different Manager never approved.

---

## 3. Frontend

- **`VoidSaleModal`** — one component covering both the direct-reason flow and the manager-approval flow, switched not on a client-side grant check (the frontend cannot know a Cashier's own grant status in advance — no such field exists on any DTO by design) but on the server's `VOID_APPROVAL_REQUIRED` response, which reveals the approval fields in place. Maps all 7 void/approval error codes to friendly, non-leaking messages.
- **Sale Detail page**: Void button gated on `useCan('sales:void') && d.canVoid` (backend-authoritative, never re-derived client-side); a danger-toned void-audit box (voided-by, approved-by if set, reason, timestamp) directly under the header; `Start Return` already excluded a Voided sale via its existing status check. Late addition: when a sale is ineligible, a small muted hint now explains why (`voidIneligibilityCode` → a shared message map also used by the modal — this field existed on the DTO from Task 4 but was never rendered anywhere until the final review caught it).
- **Receipt page**: a distinct red "VOID — SALE CANCELLED" banner for a voided sale, visually and textually separate from the refund banner — this page was never touched by any of the 10 original implementation tasks and was only discovered missing by real browser testing.
- **Staff page**: a minimal inline "Allow void" checkbox on Cashier rows (not a modal — deliberately, per the spec's "no generic permissions editor" instruction). A Manager viewer sees only that toggle and nothing else — Invite/Change role/Change branch/Deactivate/Reactivate and even the Resend/Revoke buttons on pending invitations are all gated to Owner/Admin. `/staff`'s route guard was initially left admitting only `staff:manage` (Owner/Admin) even though the sidebar link and the page's own internal rendering were correctly built for Manager access — `RequireCapability` was widened to accept an array with OR semantics and fixed in the same late fix as the receipt banner.
- **POS session controls**: Cash In / Cash Out buttons next to Close Session, one `CashMovementModal` parameterized by type rather than two near-duplicates. No standalone cash-movement ledger page was built — deliberately out of scope.
- **Close Session modal**: full reconciliation breakdown (Opening → Gross → Voided → Net → Refund → Cash In → Cash Out → Expected → Counted → Difference), "Net cash sales" always computed inline as `gross − voided` for display, never a separately stored number.
- **Sales list**: `Voided` status badge (pre-existing literal in the `SaleStatus` union, previously unused).

---

## 4. Security

- **Cashier direct grant**: verified working end-to-end in the browser — reason-only, no approval fields.
- **Cashier approval requirement**: verified — modal reveals Manager account/password fields only after a rejected reason-only attempt; void succeeds with correct credentials.
- **Wrong password**: generic "Invalid manager credentials." — verified in the browser; sale unchanged; modal stays usable.
- **Wrong-branch Manager**: verified rejected with `VOID_APPROVER_WRONG_BRANCH`.
- **Owner/Admin approval**: verified — can approve for any branch.
- **Manager grant-branch restriction**: verified both directions (own-branch Cashier succeeds, cross-branch Cashier 403s with `BRANCH_FORBIDDEN`).
- **Self-grant denial**: verified rejected.
- **Permission-revoke freshness**: verified live — a revoke takes effect on the granted Cashier's very next void attempt with no re-login, since the grant is read fresh from the tenant DB every time, never cached in the JWT.
- **Foreign-sale access**: a branch-scoped user gets 404 (not 403) on a sale outside their branch — verified in the browser (identical response whether the sale is truly missing or just invisible to them), matching `ReturnService`'s existing pattern.
- **Password handling**: never logged, never persisted, never echoed in a response or exception; the timing-equalizer hash keeps unknown-email response time indistinguishable from a known one, including across tenants.
- **Late-wave finding closed**: `UserPermissionGrant` rows no longer survive a role change away from Cashier or a branch change — previously, a demoted-then-repromoted Cashier could silently regain direct void authority, and a branch-moved Cashier could carry authority a different branch's Manager never approved.

---

## 5. Financial Correctness

- **Sale number preserved**: verified in the browser — a voided sale keeps its original number (`#0000001`), no new document is allocated.
- **Inventory reversal**: verified in the browser across a real 4-void sequence — BGC Widget stock went 50 → 47 (3 sales) → 49 (2 of those voided), Main branch's own stock (20 → 19) completely untouched by the BGC voids, confirming branch scoping.
- **No duplicate reversal**: proven by concurrency tests, not just assumed — a real concurrent double-void race, run repeatedly with no flakes, restores inventory exactly once every time (asserted via an exact quantity delta, not just a status check).
- **Cash-sale void effect**: verified via the exact reconciliation formula, both in integration tests (hand-computed decimal math matching to the peso) and live in the browser (opening ₱1,000 + gross ₱300 − voided ₱200 + cash-in ₱500 − cash-out ₱200 = expected ₱1,400, counted ₱1,390, difference −₱10 short — every line matched).
- **Cash In / Cash Out**: both verified live, correctly reflected as + and − terms respectively in the close-session breakdown.
- **Expected/counted/difference**: verified persisted server-side (read back from the close response, not a client computation) and durable (a follow-up `GET .../current` on the same register correctly 404s — session genuinely closed).
- **Returns/void mutual exclusion**: verified both directions — a sale with a return cannot be voided (`SALE_HAS_RETURNS`), a voided sale cannot be returned against (`RETURN_NOT_ALLOWED`).
- **Inactive-branch historical void**: not applicable in this phase's locked design — void requires the same-UTC-day, session-still-open window, which a branch-scoped user can never reach after their branch is deactivated (they're locked out immediately per Phase 5); only Owner/Admin could theoretically reach an inactive-branch sale within the cutoff window, and this path was not specifically exercised in browser testing (not one of the 12 required scenarios).
- **Late-wave hardening**: an out-of-range `CashMovementType` (e.g. a raw `{"type":99,...}` payload) is now rejected at both the validator and domain layer rather than silently persisting as a real drawer movement invisible to reconciliation. A double-close race (e.g. a force-close racing the session owner's own close) is now rejected on the loser rather than silently overwriting the winner's reconciliation numbers.

---

## 6. Tests

**Final counts:** 98 unit / 195 integration, **0 failed, 0 skipped** (Phase 5 baseline was 88/162; this phase added 10 unit + 33 integration net).

Major new scenario groups: `SaleVoidTests` (domain guard, timestamp-injection proof), `SalesVoidPermissionTests` (grant/revoke, branch scoping, self-grant denial, role/branch-change cleanup — 3 tests added in the final fix wave), `RegisterCashMovementTests` (ownership, closed-session rejection, enum validation), `VoidSaleTests` (11 tests covering the full authorization/eligibility matrix), `VoidConcurrencyTests` (double-void, void-vs-return — both proven via repeated runs to restore inventory exactly once / never both succeed), `ReconciliationTests` (exact decimal math), `CloseVoidConcurrencyTests` and `CashMovementCloseConcurrencyTests` (void-vs-close and cash-movement-vs-close — both proven via calibrated head-start variants to exercise both sides of the race, not just structurally capable of it), `SessionCloseConcurrencyTests` (close-vs-close, added in the final fix wave).

A recurring risk class surfaced twice in this phase and is worth naming explicitly: a concurrency test's "10/10 green" run can be 10 samples of only one branch of the race if nothing forces the other side to occur. This was caught once organically (Task 5, `CloseVoidConcurrencyTests` — the original unbiased test turned out to always favor `close`, backwards from what was assumed, hiding a real assertion bug for the entire time the "wrong" branch went unexercised) and once by the final review naming it as a named risk before it shipped (`CashMovementCloseConcurrencyTests`, fixed in the last wave). Every affected test now has both an unbiased case and an empirically-calibrated head-start variant with a loud-failure assertion if the calibration ever stops working.

---

## 7. Browser Verification

Executed against the real running API + frontend (no mocks), one tenant, 8 seeded accounts (Owner, Admin, BGC Manager, two BGC Cashiers — one granted/one not — a Main-branch Cashier, and one each of InventoryStaff/Viewer/KitchenStaff), driven by headless Chrome over CDP.

| # | Scenario | Result |
|---|---|---|
| 1 | Cashier without permission | PASS |
| 2 | Invalid approval | PASS |
| 3 | Cashier with permission | PASS |
| 4 | Permission revoke (live, no re-login) | PASS |
| 5 | Manager permission management | **Initially FAILED** (real bug — Manager blocked from `/staff` route entirely) → **fixed → re-verified PASS** (Manager now opens `/staff`, sees only 6 BGC rows, only the toggle active, and personally granted+revoked `SalesVoid` through their own session) |
| 6 | Voided Sale UI | **Initially PARTIAL FAIL** (Sale Detail correct; receipt showed no void indication) → **fixed → re-verified PASS** (distinct red VOID banner; a completed sale's receipt unaffected) |
| 7 | Inventory reversal | PASS |
| 8 | Cash In | PASS |
| 9 | Cash Out | PASS |
| 10 | Close Session reconciliation | PASS (exact math, verified persisted) |
| 11 | RBAC matrix (all 7 roles) | PASS |
| 12 | Branch boundary | PASS (404-not-403 confirmed; Owner crosses branches, Manager/Cashier cannot) |
| — | Regression smoke (11 routes) | PASS |

**Console errors:** zero JavaScript runtime errors or uncaught exceptions across the entire pass, both original and re-verification rounds. The only network-log entries were the expected non-2xx responses from scenarios deliberately exercising error paths (400s from rejected void attempts, 404s from cross-branch access attempts).

**One pre-existing, non-Phase-6 discrepancy noted during regression smoke:** a Cashier can reach `/settings` (a read-only view with an inline notice), where the brief's literal wording expected "cannot reach." Confirmed via `git log` that neither `SettingsPage.tsx` nor its route was touched by any Phase 6 commit — this pattern predates the phase entirely.

Both real bugs found by browser testing were root-caused by reading actual source (not guessed), fixed, code-reviewed clean, and then specifically re-verified live in the browser — not just assumed fixed from the code review alone.

---

## 8. Git State

- **Branch:** `feature/void-cash-operations`
- **HEAD:** `f89d186`
- **Commits:** 27, ahead of `master` (base `d35589b`)
- **Working tree:** clean
- **Push status:** not pushed — no upstream configured
- **Merge status:** **not merged.** Task-scoped review (10/10 tasks, one fix round each on 4 of them), full-solution verification, 12/12 browser scenarios, and a final whole-branch review (0 Critical findings, 5 Important findings — all fixed and re-reviewed clean in one further wave) are all complete. The branch is ready for merge review but was deliberately left unmerged per this phase's explicit instruction.

---

## 9. Remaining Limitations

Carried forward unchanged: no partial void, no line-level post-sale void, no multiple branch assignments, no stock transfers, no branch-specific tax/pricing/catalog, no consolidated reporting, no Reporting phase, no offline selling, no F&B, no tenant timezone, PHP currency fixed, one email = one tenant, no separate cashier PIN, no production invitation email provider (still surfaced via a non-production API response).

New to this phase:
- **No closed-session void of any kind** — void requires the original session still open and the same UTC calendar day; anything else routes to Return. This was an explicit, locked design decision, not an oversight.
- **No permission-grant/revoke history** — `UserPermissionGrant` row existence is the sole state; grant/revoke leaves no audit trail beyond the void's own `ApprovedByUserId` on whichever votes it was actually used for.
- **No standalone cash-movement ledger viewer** — Cash In/Out creation exists; a dedicated list/audit page does not (the reconciliation breakdown is the only place movements are surfaced).
- **Deferred, explicitly out of this phase's 3 named concurrency races (carried to a follow-up):** `CheckoutService` and `ReturnService` write reconciliation inputs (`Payment`/`RefundPayment` rows) without taking the session lock — a checkout or return committing between a close's SUM query and its own commit could theoretically leave that cash out of the closed session's expected total. This is pre-existing, not introduced by this phase, and was named explicitly by the final review as the natural next thing to bring under the same lock protocol this phase established.
- **A cross-service lock-ordering race** between `VoidSaleService` and `ReturnService` on `BranchInventories` rows for a multi-line sale (both financially safe via transaction rollback, but surfaces as a raw SQL error rather than a clean typed one) — deferred, not exercised by the single-line-sale test suite.
- **`DeactivateAsync`/`ReactivateAsync` don't reconsider `SalesVoid` grants** — arguably correct (same person, same role, same branch on reactivation) but not explicitly covered by this phase's grant-cleanup work.
