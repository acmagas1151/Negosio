# Handover Summary

_End of a session on branch `feature/receipt-settings` (HEAD after Task 13 fix = `83ee5e7`, **not merged, not pushed** — awaiting the user's review). Branched from `master` @ `70abed7`._

## Project Context

- **Project:** Negosio — multi-tenant retail/POS platform.
- **Stack:** Backend .NET 9 / ASP.NET Core / EF Core 9 / SQL Server (LocalDB), modular monolith (Api → Infrastructure → Application → Domain), database-per-tenant. Frontend React 19 / TS strict / Vite / React Router 7 / TanStack Query v5 / Tailwind v4. JWT, role policies (Owner/Admin/Manager/Cashier/InventoryStaff/KitchenStaff/Viewer). Currency PHP.
- **This session's work:** the **Receipt Settings** module (Plan A of a two-plan feature). Configurable receipt header / footer / business-info / thermal width, tenant-default + per-branch, plus the plumbing the Sales Receipt now renders from. **Plan B (the persistent Delivery Receipt document) is designed but NOT started** — see `docs/superpowers/plans/2026-09-09-delivery-receipt.md`.

## Where things stand

- Design spec: `docs/superpowers/specs/2026-09-09-receipt-settings-design.md` (APPROVED, D1–D9 resolved).
- Implementation plans: `docs/superpowers/plans/2026-09-09-receipt-settings.md` (Plan A — executed) and `…-delivery-receipt.md` (Plan B — pending).
- Plan A executed subagent-driven, task-by-task, each task reviewed. Commits `25a62af..83ee5e7` on `feature/receipt-settings`.
- **Full backend integration suite: 226/226 green** (214 baseline + 12 new). Unit 121/121. Frontend `tsc --noEmit` + `npm run build` clean.
- Live-verified against the running app (:5170 API + :5173 Vite): tenant-default GET/PUT round-trip, branch-override seeding, DELETE reset, oversized-input 400, Manager branch-scope (8/8 — PUT tenant-default 403, other branch 403, own branch 200, DELETE own 204), Cashier 403 on writes, a fresh-tenant receipt byte-consistent with the old output, and a configured receipt (58 mm, custom multi-line header/footer, cashier row hidden, per-payment Tendered/Change) rendered in headless Chrome with **zero console errors**.

## What shipped (Plan A)

**Backend — new `Application/Settings/`:**
- `ReceiptSettings` aggregate (`Domain/Entities/Settings/ReceiptSettings.cs`) — one row per scope. `TenantId`, `Guid? BranchId` (null = tenant default), 14 presentation fields (`ReceiptWidth Width` enum `Mm80`/`Mm58`; `Sales*`/`Delivery*` header/footer text + toggles — all **non-nullable** `bool`), `UpdatedByUserId`. Factories `CreateDefault` / `CreateFrom(seed)`; `Update(values, userId)` overwrites **every** field (whole-row snapshot, no partial patch); `ToValues()`.
- `ReceiptSettingsValues` (`Domain/Common/`) — the shared 14-field record + `static HardcodedDefault` (no custom header/footer, every toggle `true`, `Mm80`) = **the fallback that reproduces the pre-feature receipt exactly**.
- `ReceiptSettingsService` — `GetAsync(branchId?)` / `UpdateAsync(branchId?, request)` / `ResetAsync(branchId)`. **Whole-row resolution:** branch row → tenant-default row → `HardcodedDefault` (first that exists wins; no field merge). First branch write seeds the new row from the currently-effective values, then applies the request. Manager cannot write the tenant default (`!IsAllBranch` → `BranchForbidden`); branch scope enforced via `IBranchAccessResolver`. Header/footer `ReceiptText.Normalize`-d before persist; request validated first.
- `IReceiptSettingsResolver` — scoped, per-request memoized, `AsNoTracking`; the read-path the receipt render uses.
- `ReceiptSettingsController` — `GET /api/settings/receipts?branchId=` (policy `SalesView`), `PUT` + `DELETE` (new policy **`ReceiptSettingsManage` = Owner/Admin/Manager**). DELETE takes a required `branchId` (can never wipe the tenant default).
- EF: `ReceiptSettingsConfiguration` + migration `20260909151044_AddReceiptSettings` (table + 2 **filtered unique indexes**: one tenant-default row per tenant, one row per branch).
- **Business identity fields** (design D3): `TenantProfile.ContactNumber` + `TenantProfile.TaxId`, `Branch.ContactNumber` (migration `20260909145658_AddBusinessContactInfo`). Endpoint `GET/PUT /api/settings/business-info` (`TenantSettingsService`, policy `TenantSettingsWrite` = Owner/Admin).
- `ReceiptDto` / `ReceiptPaymentDto` extended (append-only): `HeaderText`, `FooterText`, `BusinessAddress`, `BusinessContactNumber`, `TaxId`, `ShowBranch/Cashier/PaymentMethod/TaxLine/ReferenceNumber`, `Width`; per payment `ReferenceNumber`, `ReceivedAmount`, `ChangeAmount`. `ReceiptService` joins the resolved settings + `TenantProfile` + `Branch`. **Authoritative sale values are unchanged** — the front end still renders persisted totals verbatim.
- `DocumentNumberType.DeliveryReceipt = 5` — **NOT added yet** (it belongs to Plan B Task 1; the Plan A ledger self-review note about adding it in Task 3 was not carried out — intentional, Plan B owns it).

**Frontend:**
- `components/receipt/` — `ReceiptHeader`, `ReceiptFooter`, `ReceiptBusinessInfo`, `receiptStyles.thermalReceiptCss(width)`. `ReceiptPage.tsx` rewired onto them + the new toggles + per-payment Tendered/Change (the aggregate `Change` row is suppressed when any payment carries `receivedAmount`). `components/ui/TextArea.tsx` added.
- `pages/settings/ReceiptSettingsPage.tsx` — scope `<Select>` (Tenant default + branches; Manager auto-lands on their branch), sub-tabs **General / Sales receipt / Delivery receipt**, right-pane **client-only live preview** ("Preview — not saved"), Save/Discard mirroring `TaxSettingsCard`, **Reset to tenant defaults** (branch overrides only, `ConfirmDialog`), "Last updated by … · date". `components/settings/SettingsTabs.tsx` (Tax | Receipts) + `ReceiptPreview.tsx`.
- `SettingsPage` now serves `/settings/tax`; `/settings` redirects there; `/settings/receipts` is capability-guarded. `nav.ts` gains a **`Receipt settings`** item (capability `receipt:settings` = Owner/Admin/Manager). `api/receiptSettings.ts` + `useReceiptSettings` hook + `types.ts` DTOs + `useCan` capability.

## Key contracts / rules (do not break)

- **Fallback is mandatory.** A tenant with zero `ReceiptSettings` rows must print exactly as before. No seed row; the resolver returns `HardcodedDefault`.
- **Whole-row snapshot.** No nullable/tri-state toggles. `PUT` always carries all 14 fields and overwrites the row. "Reset" = `DELETE` the branch row → resolution falls through to the tenant default.
- **`ReceiptSettingsManage` = Owner + Admin + Manager**; Manager branch-scoped server-side; never rely on the hidden nav item.
- **Preview ≠ saved config.** The right-pane preview is client-rendered from unsaved form state; printed/reprinted receipts always fetch persisted settings. Reprint of a sale reflects **current** settings (design-approved).
- Header/footer: plain text only, ≤ 500 chars, ≤ 6 lines, whitespace-normalized. Contact/TIN ≤ 40.

## Open items for the user (from live verification — not yet fixed)

1. **Tenant contact number never reaches the receipt.** `ReceiptService` sets `BusinessContactNumber = branch.ContactNumber` with no fallback, so the Receipt Settings → General "Contact number" (which edits `TenantProfile.ContactNumber`) is invisible on receipts. Spec §6.3 implies the tenant contact is joined. Suggested fix: `branch.ContactNumber ?? profile.ContactNumber` in `ReceiptService.GetReceiptAsync`.
2. **`canEdit` ignores role.** `ReceiptSettingsService.GetAsync` computes `canEdit` from branch assignment only, so a Cashier `GET`ting their own branch's settings sees `canEdit=true`. `PUT` still 403s and the page is route-guarded, so impact is low, but the DTO field is semantically wrong. Suggested fix: also require `Role ∈ {Owner, Admin, Manager}`.
3. Deferred polish collected across the task reviews (all Minor): `ReceiptSettingsForm` is ~470 lines (extract the 3 sub-tab panels); `deliveryShowContactNumber` toggles two preview rows; `bg-white` raw color in the delivery preview; the mock numbers in the sales preview are internally inconsistent; the legacy `.Tenant` vs `.TenantDb` migration-namespace split (10 legacy files) — this feature standardized on `.TenantDb` per spec/README/baseline.
4. **Plan B (Delivery Receipt)** is the next piece: persistent `DeliveryReceipt` aggregate, `DR-000001` numbering, A4 print page, "Print delivery receipt" on the Sale detail page. Plan doc ready; the user confirmed DR data is frozen at creation but presentation follows live settings.

## Next steps

1. User reviews `feature/receipt-settings` (live and/or diff). Decide on open items 1–2 above (small backend fixes).
2. Merge `feature/receipt-settings` to `master` once approved (currently unmerged, unpushed).
3. Then execute Plan B (`docs/superpowers/plans/2026-09-09-delivery-receipt.md`).

## Dev environment

- API `http://localhost:5170` (`dotnet run` from `src/Negosio.Api`). Frontend `http://localhost:5173` (`npm run dev` from `web/negosio-web`; talks to :5170).
- Integration tests spin up their own per-run tenant DBs; the full suite is ~14 min.
- SDD ledger for this feature: `.superpowers/sdd/2026-09-09-receipt-settings/progress.md` (every task, review, ruling).

---

## Prior session (historical context) — Reports module

The `master` branch this feature is built on already contains the **Reports module Phase B** (Overview: KPIs, sales trend, payment breakdown, top products, category performance at `/reports`) merged as `70abed7`, plus `SaleReturn.ApprovedByUserId`. Reports **Phase C** (Branch / Register / Cashier performance) was planned but not built. Reports timezone handling is a fixed Asia/Manila (UTC+8) v1 simplification. See git history and the earlier specs under `docs/superpowers/specs/` for detail.
