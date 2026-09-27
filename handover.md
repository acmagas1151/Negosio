# Handover Summary

_Generated: 2026-09-27 (updated same day — fulfillment-cancel authorization fix)_

## Project Context

- **Project:** Negosio — a multi-tenant retail/POS management platform (registers, inventory, staff, delivery/pickup fulfillment, reporting).
- **Tech stack:**
  - Backend: .NET 9 / ASP.NET Core Web API / EF Core 9 / SQL Server (LocalDB). Modular monolith: `Api → Infrastructure → Application → Domain`. Database-per-tenant (separate Platform DB + one DB per tenant, per-request connection routing). JWT auth with role-based policies (Owner/Admin/Manager/Cashier/InventoryStaff/KitchenStaff/Viewer). Currency fixed to PHP.
  - Frontend: React 19 / TypeScript (strict) / Vite / React Router 7 / TanStack Query v5 / Tailwind v4 / Recharts.
- **Working directory:** `C:\Users\Ace\Documents\Negosio`. Git repo, currently on `master`.
- **IMPORTANT — uncommitted state:** as of this handover, `master`'s working tree has real, verified, but **uncommitted** changes (see "Current State" below). A fresh session must not assume a clean tree.

## Current Task

The user reported a live bug while testing the previously-merged Reports Phase C / fulfillment-fixes work (see "Prior completed work" below): **a Cashier could not cancel a delivery/pickup schedule at checkout time — only a Manager could.**

Investigation confirmed this was a real permission-system gap, not a false alarm: every other sensitive POS action (Void sale, Process return, Apply discount, Open cash drawer) follows a "Owner/Admin/Manager act directly; a Cashier needs either a granted permission or live Manager/Admin/Owner approval" pattern — but the dedicated "Cancel" button on a pending delivery/pickup schedule was hardcoded to Manager+ only, with **no grant and no approval escape hatch at all** for a Cashier. The user chose to fix it (option: "Add grant + approval (recommended)") so Cashiers get the same pattern as everywhere else.

**This fix is now complete and verified, but sits uncommitted on `master`'s working tree** — the user has not yet been asked/asked to commit it.

## Completed Work

### This session's fix: Fulfillment-cancel authorization (Cashier grant + approval)

**Backend:**
- `src/Negosio.Domain/Enums/UserPermission.cs` — added `FulfillmentCancel = 5`.
- `src/Negosio.Application/Common/ErrorCodes.cs` — added `FulfillmentCancelApprovalRequired`.
- `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs` — added `ApprovedByUserId` property; `Cancel(...)` signature gained a `Guid? approvedByUserId` parameter (inserted before the timestamp, mirroring `Sale.Void`'s `approvedByUserId` param).
- `src/Negosio.Infrastructure/Persistence/Configurations/DeliveryReceiptConfiguration.cs` + new migration `20260927025105_AddFulfillmentCancelApprovedByUserId` — clean additive nullable-column migration, no data changes.
- **New file** `src/Negosio.Application/Delivery/FulfillmentCancelAuthorizationResolver.cs` (`IFulfillmentCancelAuthorizationResolver`) — mirrors `VoidAuthorizationResolver`/`ReturnAuthorizationResolver` exactly: Owner/Admin/Manager act directly; Cashier with the `FulfillmentCancel` grant acts directly; Cashier without it gets `FulfillmentCancelApprovalRequired` and can retry with `VoidSaleApprovalInput` (manager email+password).
- `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` — injected the resolver; `CancelWithDispositionAsync` now resolves authorization **after** the existing eligibility checks (branch/method/status/saleId) but **before** opening the transaction — same ordering as `VoidSaleService.VoidAsync`. The resolved `approvedByUserId` flows through `CancelWithDispositionCoreAsync` into `dr.Cancel(...)` and into `MapToDtoAsync`'s actor-name lookup (new `ApprovedByName` field on `FulfillmentScheduleDto`). The void cascade (`CancelActiveScheduleForVoidedSaleAsync`) always passes `approvedByUserId: null` and never consults this resolver — it's already authorized one layer up by `VoidAuthorizationResolver`.
- `src/Negosio.Application/Delivery/DeliveryReceiptContracts.cs` — added `Approval` field to `CancelDeliveryRequest`/`CancelPickupRequest`; added `ApprovedByName` to `FulfillmentScheduleDto`.
- `src/Negosio.Api/Authorization/AuthorizationPolicies.cs` — broadened the `FulfillmentCancel` policy from `ManagementRoles` to `PosRoles` (every POS role may attempt; the resolver enforces the real split) — same precedent as `RefundManage`/`SalesView`.
- `src/Negosio.Application/DependencyInjection.cs` — registered the new resolver.
- Staff permission-grant plumbing extended with the new permission (5 call sites): `StaffContracts.cs` (`StaffMemberDto`/`ChangeStaffPermissionsRequest` gained `FulfillmentCancel`), `StaffController.cs`, `StaffService.cs` (3 `StaffMemberDto` construction sites), `UserPermissionGrantService.cs`.

**Frontend:**
- `web/negosio-web/src/lib/useCan.ts` — `'delivery:cancel'` capability now admits Cashier (server resolves the grant-vs-approval split).
- `web/negosio-web/src/components/sales/CancelFulfillmentModal.tsx` — added the same approval UI pattern as `VoidSaleModal.tsx`: reveals manager-credential fields on first `FULFILLMENT_CANCEL_APPROVAL_REQUIRED` denial, handles `INVALID_APPROVER_CREDENTIALS`/`VOID_APPROVER_WRONG_BRANCH`.
- `web/negosio-web/src/components/staff/StaffPermissionsModal.tsx` — new "Fulfillment" permission group with a "Cancel delivery/pickup" toggle.
- `web/negosio-web/src/pages/SaleDetailPage.tsx` — cancelled-schedule display now shows "Approved by X" when applicable; refreshed a stale comment about the `delivery:cancel` capability key.
- `web/negosio-web/src/api/types.ts` — mirrored all the above DTO/request shape changes.

**Tests:**
- `tests/Negosio.UnitTests/Delivery/DeliveryReceiptEntityTests.cs` — updated all 11 `dr.Cancel(...)` call sites for the new parameter; added 2 new tests for the approver-stamping behavior.
- `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptStatusTests.cs` and `PickupTests.cs` — replaced the now-obsolete `Cashier_cannot_cancel_a_delivery`/`_pickup` tests (which asserted the old, buggy 403-Forbidden behavior) with `Cashier_without_grant_requires_approval_then_succeeds_with_valid_manager_credentials` and `Cashier_with_a_direct_grant_cancels_without_any_approval`, mirroring `VoidSaleTests.cs`'s pattern.
- `tests/Negosio.IntegrationTests/Platform/TenantMigrationTests.cs` — a pre-existing migration round-trip test hardcoded "2 pending migrations" after a rollback checkpoint; bumped to 3 to account for the new migration (not a regression — an expected update).
- `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptConcurrencyTests.cs` — added a throwing `UnusedFulfillmentCancelAuthorizationResolver` stub for its hand-built `DeliveryReceiptService` (the void-cascade path it tests never consults the resolver, so a throwing stub proves that invariant).
- 8 other integration test files needed a 5th positional argument added to existing `ChangeStaffPermissionsRequest(...)` calls: `CashDrawerOpenTests.cs`, `VoidSaleTests.cs`, `CheckoutTests.cs`, `SalesVoidPermissionTests.cs`, `ReturnTests.cs`.

### Prior completed work (already merged to `master` as commit `8039d44`, still not pushed to `origin/master`)
Two subagent-driven plans: (1) fulfillment recovery fixes — a "Schedule now" recovery action on Sale Detail for a checkout-succeeded-but-schedule-failed sale, and a void-cascade that cleanly cancels a pending schedule when its sale is voided; (2) Reports Phase C — new Branch/Register/Cashier performance tabs at `/reports/performance`. Full detail, metric definitions, and the rulings made during that work are in `C:\Users\Ace\Desktop\negosio-status.md` under "#13 in detail" — not repeated here to avoid drift between the two files.

## Current State

**Working (verified, but uncommitted):**
- Backend: **182/182 unit tests + 337/338 integration tests** passing. The 1 apparent integration failure is the pre-existing `DeliveryReceiptConcurrencyTests.Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds` flake (a genuine concurrency-race test sensitive to full-suite load, unrelated to this change, untouched code path) — confirmed passing in isolation.
- Frontend: `tsc -b` and `npm run build` both zero-error.
- Both dev servers restarted and confirmed live: API at `http://localhost:5170` (401 on a protected route = up), frontend at `http://localhost:5173` (200 = up).
- The fix was verified via 4 new/rewritten integration tests exercising the full denied→approved-with-manager-credentials and direct-grant-no-approval-needed flows for both Delivery and Pickup. No separate manual browser click-through was done for this specific fix (the API-level integration tests already cover the exact scenario end-to-end, and the fix is UI-thin — the modal changes are a near-verbatim copy of `VoidSaleModal.tsx`'s already-proven pattern).

**Git state — this is the important part for a fresh session:**
- `git status` on `master` shows **30 files changed, 2 new files** (`FulfillmentCancelAuthorizationResolver.cs` and the 2 migration files), all uncommitted.
- This was done directly on `master`'s working tree, NOT on a feature branch — unlike every prior body of work in this project's history.
- **Needs verification:** whether the user has since asked for (or the assistant has made) a commit. If `git status` still shows this many uncommitted changes when a new session starts, the fix described above is what's sitting there uncommitted.

**Nothing currently known to be broken.**

## Known Issues / Bugs

- Same pre-existing flaky test as before (see above) — not a regression.
- Carried-over, still-open, out-of-scope-for-this-fix items are listed in `C:\Users\Ace\Desktop\negosio-status.md`'s "#13 in detail" section (stale delivery-report columns, missing drill-down links, a few Reports sessions-view UX polish items). None of them relate to this session's fix.

## Important Decisions

- **Authorization-resolve ordering mirrors `VoidSaleService.VoidAsync` exactly:** eligibility checks (branch guard, method match, Pending status, has-a-sale-id) run first and fast-fail before the authorization resolver is even consulted; the resolver runs before the transaction opens. Don't reorder this without re-checking why Void does it this way (avoids resolving/verifying manager credentials for a request that was going to fail anyway).
- **`ApprovedByUserId` was added as a real persisted column**, not just an in-memory check, because every other grant-or-approval action in this codebase (`Sale.ApprovedByUserId`, `SaleReturn.ApprovedByUserId`, `CashDrawerOpenEvent.ApprovedByUserId`) persists it — 3/3 precedent, so this was treated as the established convention rather than a judgment call.
- **The void cascade never consults the new resolver.** `CancelActiveScheduleForVoidedSaleAsync` always passes `approvedByUserId: null` — voiding a sale is already authorized by `VoidAuthorizationResolver` one layer up, and re-checking fulfillment-cancel authorization on top would be double-gating the same action. A throwing stub in the concurrency test file enforces this invariant.
- **`FulfillmentCancel` authorization policy was broadened from Manager+ to every POS role** (`PosRoles`), with the real Cashier-vs-Manager split now enforced entirely by the new resolver — this is the same pattern `SalesView`/`RefundManage` already use, not a new pattern invented for this fix.
- **No new "cancel fulfillment" report/attribution field was added to Reports** (e.g., to `CashierPerformanceRowDto`'s existing `VoidApprovalsCount`/`ReturnApprovalsCount` pattern). The user's request was scoped to fixing the permission gap itself; extending Reports Phase C's approval-attribution to also cover fulfillment-cancel approvals was not asked for and would be a reasonable follow-up, not something silently skipped by oversight.

## Files to Review

- `src/Negosio.Application/Delivery/FulfillmentCancelAuthorizationResolver.cs` — the new resolver (read this first — it's the whole fix in miniature).
- `src/Negosio.Application/Delivery/DeliveryReceiptService.cs` — `CancelWithDispositionAsync`/`CancelWithDispositionCoreAsync` for how it's wired in.
- `src/Negosio.Domain/Entities/Delivery/DeliveryReceipt.cs` — the `Cancel(...)` signature change and `ApprovedByUserId`.
- `web/negosio-web/src/components/sales/CancelFulfillmentModal.tsx` — the approval UI.
- `tests/Negosio.IntegrationTests/Delivery/DeliveryReceiptStatusTests.cs` and `PickupTests.cs` — the end-to-end proof the fix works.
- `C:\Users\Ace\Desktop\negosio-status.md` — full project history across all phases; this handover only covers this session's fix in depth.

## Next Steps

1. **Ask the user whether to commit this fix.** It is fully verified but was never committed — per this project's standing rule ("only commit when explicitly asked"), a fresh session should not assume it's safe to commit without checking in first, even though the work is done.
2. Once committed (or if already committed by the time a new session reads this — check `git log`/`git status` first), decide whether to push `master` to `origin/master` along with the other unpushed work from `8039d44` — this is a user decision.
3. Consider whether to extend Reports Phase C's `VoidApprovalsCount`/`ReturnApprovalsCount` pattern to also track fulfillment-cancel approvals now that they're persisted (`ApprovedByUserId` on `DeliveryReceipt`) — flagged above as a reasonable follow-up, not committed to.
4. The other known limitations tracked in `negosio-status.md` (stale delivery-report columns, drill-down links, sessions-view UX polish) remain open and unrelated to this fix.

## Prompt for Next Claude Session

```
I'm continuing work on Negosio, a multi-tenant retail POS platform (.NET 9 / EF Core 9 / SQL Server backend, React 19 / TypeScript / Vite frontend). Read C:\Users\Ace\Documents\Negosio\handover.md for full context on the most recent fix (Cashiers previously couldn't cancel a delivery/pickup schedule at all — fixed by adding the same grant-or-approval pattern Void/Return/Discount/CashDrawer already use). That fix is verified (backend + frontend tests green, both dev servers confirmed live) but was left UNCOMMITTED on master's working tree — check `git status` first before assuming anything about commit state.

Before doing anything else:
1. Run `git status` and `git log -3` to see whether the fulfillment-cancel fix described in the handover has since been committed.
2. Check whether the API (localhost:5170) and frontend dev server (localhost:5173) are still running; restart them if not (commands are in the handover's Current State section notes and Desktop status file).
3. Read C:\Users\Ace\Desktop\negosio-status.md for the full project history/status across all phases.
4. Do not push to origin/master, merge, or commit anything without asking me first — that's an explicit standing rule on this project.

Then [describe what you want done next].
```
