# Handover Summary

_**Plan A (Receipt Settings) + Plan B (Delivery Receipt) both executed**, whole-branch reviewed (verdict: ship with follow-ups), final-review fixes applied. **Merged to `master` locally as `a5c2df1` (`--no-ff`); `feature/receipt-settings` deleted. NOT pushed** — `master` is 36 commits ahead of `origin/master`. Forked from `master` @ `70abed7`._

## Project Context

- **Project:** Negosio — multi-tenant retail/POS platform.
- **Stack:** Backend .NET 9 / ASP.NET Core / EF Core 9 / SQL Server (LocalDB), modular monolith (Api → Infrastructure → Application → Domain), database-per-tenant. Frontend React 19 / TS strict / Vite / React Router 7 / TanStack Query v5 / Tailwind v4. JWT, role policies (Owner/Admin/Manager/Cashier/InventoryStaff/KitchenStaff/Viewer). Currency PHP.
- **This branch's work:** a two-plan feature — **Plan A: Receipt Settings** (configurable receipt header / footer / business-info / thermal width, tenant-default + per-branch, the Sales Receipt renders from it) and **Plan B: Delivery Receipt** (a second, persistent printable document generated from a completed Sale).

## Where things stand

- Design spec: `docs/superpowers/specs/2026-09-09-receipt-settings-design.md` (APPROVED; D1–D9 resolved; **§4.3 DR numbering REMOVED 2026-09-10 at the user's instruction**).
- Plans: `docs/superpowers/plans/2026-09-09-receipt-settings.md` (Plan A — executed) and `…-delivery-receipt.md` (Plan B — executed; carries a "DR NUMBERING REMOVED" revision banner).
- Both executed subagent-driven, task-by-task, each task spec+quality reviewed, fix rounds where needed. SDD ledgers: `.superpowers/sdd/2026-09-09-receipt-settings/progress.md` and `.superpowers/sdd/2026-09-09-delivery-receipt/progress.md`.
- **Tests (controller-run, authoritative): Unit 137/137, Integration 240/240, 0 failed** (HEAD `925ddf1`; +1 voided-sale guard test). Frontend `tsc --noEmit` + `npm run build` clean.
- **Live-verified** (`:5170` API + `:5173` Vite): Plan B HTTP pass 36/36 (create → 201 + `Location`, 2nd POST → 200 same doc / data not overwritten, item snapshot survives a catalog rename, prices hidden when `DeliveryShowPrices` off then shown when on, presentation follows live settings while DR data stays immutable, blank recipient / blank address / empty `items[]` / duplicate `saleItemId` all → 400, void → status `Voided`). Headless-Chrome pass: A4 page with custom header (title only) and with null header (business block + `DELIVERY RECEIPT` title + `Unit Price`/`Amount` columns), `Contact:`/`Related Sale:` gating, stacked signature block, the create modal (Recipient name / Recipient address / Contact number / Delivery notes + per-line Deliver qty), "Print delivery receipt" → "View delivery receipt" after creation, blank-submit blocked. No JS exceptions on the DR pages.

## What shipped — Plan A (Receipt Settings)

**Backend — `Application/Settings/`:**
- `ReceiptSettings` aggregate (`Domain/Entities/Settings/ReceiptSettings.cs`) — one row per scope. `TenantId`, `Guid? BranchId` (null = tenant default), 14 presentation fields (`ReceiptWidth Width` enum `Mm80`/`Mm58`; `Sales*`/`Delivery*` header/footer text + toggles, all **non-nullable** `bool`), `UpdatedByUserId`. `CreateDefault` / `CreateFrom(seed)`; `Update(values, userId)` overwrites **every** field (whole-row snapshot); `ToValues()`.
- `ReceiptSettingsValues` (`Domain/Common/`) — shared 14-field record + `static HardcodedDefault` (no custom header/footer, every toggle `true`, `Mm80`) = **the fallback that reproduces the pre-feature receipt exactly**.
- `ReceiptSettingsService` — `GetAsync` / `UpdateAsync` / `ResetAsync`. **Whole-row resolution:** branch row → tenant-default row → `HardcodedDefault` (first that exists wins; no field merge). First branch write seeds the new row from the currently-effective values, then applies the request. `canEdit = roleCanManage && (IsAllBranch || branchId == assigned)`. Manager cannot write the tenant default.
- `IReceiptSettingsResolver` — scoped, per-request memoized, `AsNoTracking`; the read-path the receipt render uses (Sales **and** Delivery).
- `ReceiptSettingsController` — `GET /api/settings/receipts?branchId=` (`SalesView`), `PUT` + `DELETE` (`ReceiptSettingsManage` = Owner/Admin/Manager). DELETE requires `branchId` (never wipes the tenant default).
- EF: `ReceiptSettingsConfiguration` + migration `20260909151044_AddReceiptSettings` (table + 2 filtered unique indexes).
- **Business identity fields** (D3): `TenantProfile.ContactNumber` + `TaxId`, `Branch.ContactNumber` (migration `20260909145658_AddBusinessContactInfo`). `GET/PUT /api/settings/business-info` (`TenantSettingsService`).
- `ReceiptDto` / `ReceiptPaymentDto` extended append-only (header/footer/business/toggles/width; per payment `ReferenceNumber`/`ReceivedAmount`/`ChangeAmount`). `BusinessContactNumber = branch?.ContactNumber ?? profile.ContactNumber`. **Authoritative sale totals unchanged** — the front end still renders persisted values verbatim.

**Frontend:** `components/receipt/` (`ReceiptHeader`, `ReceiptFooter`, `ReceiptBusinessInfo`, `receiptStyles.thermalReceiptCss(width)`); `ReceiptPage.tsx` rewired; `components/ui/TextArea.tsx`. `pages/settings/ReceiptSettingsPage.tsx` — scope `<Select>`, sub-tabs **General / Sales receipt / Delivery receipt**, client-only live preview, Reset to tenant defaults. `SettingsTabs.tsx` (Tax | Receipts). `nav.ts` `Receipt settings` item (capability `receipt:settings`). `api/receiptSettings.ts` + `useReceiptSettings`.

_Plan A open items 1 & 2 from the first handover (tenant-contact fallback; role in `canEdit`) were **fixed** in the Plan A fix wave (`67e5d3e`). Address/TIN on the default receipt was a product call — **kept as-is** (they appear whenever the branch/tenant has them set); spec has a cross-cutting note._

## What shipped — Plan B (Delivery Receipt)

**Domain (`Domain/Entities/Delivery/`, namespace `Negosio.Domain.Entities`):**
- `DeliveryReceipt : Entity` — `TenantId`, `BranchId`, `SaleId : Guid?`, `RelatedSaleNumber : string?`, `RecipientName`, `DeliveryAddress`, `ContactNumber : string?`, `DeliveryNotes : string?`, `PreparedByUserId`, `PreparedByNameSnapshot`, `IReadOnlyCollection<DeliveryReceiptItem> Items`. **No document number** — identified solely by `Id` (Guid). `Create(...)` trims all strings; `RecipientName` + `DeliveryAddress` required (`ArgumentException` on blank). `AddItem(productNameSnapshot, variantNameSnapshot, quantity, unitPrice)` — `quantity > 0`. Follows the `SaleReturn` aggregate pattern.
- `DeliveryReceiptItem : Entity` — `ProductNameSnapshot`, `VariantNameSnapshot : string?`, `Quantity`, `UnitPrice : decimal?`; `internal` ctor. **Line items are snapshots** — a later catalog rename/reprice never changes an issued DR.

**EF:** `DeliveryReceiptConfiguration` + migration `20260910045242_AddDeliveryReceipts` (2 tables; `(TenantId, SaleId)` and `(TenantId, DeliveryReceiptId)` **non-unique** lookup indexes + the EF FK-backing indexes; FK to `Sales` `OnDelete: Restrict`, items `OnDelete: Cascade`). **No unique index** — one-per-sale is a *service* rule, not a schema rule.

**Application (`Application/Delivery/`):**
- `CreateDeliveryReceiptRequest { RecipientName, DeliveryAddress, ContactNumber?, DeliveryNotes?, IReadOnlyList<CreateDeliveryReceiptItemInput>? Items }` — `Items == null` ⇒ all sale lines at full quantity. `CreateDeliveryReceiptRequestValidator`: `RecipientName` NotEmpty ≤120 ("Recipient name is required."), `DeliveryAddress` NotEmpty ≤300 ("Recipient address is required." — user-facing label), `ContactNumber` ≤40, `DeliveryNotes` ≤1000, each item `Quantity > 0`.
- `DeliveryReceiptDto` — no `Number`. `Items[]`, business fields, `HeaderText?`/`FooterText?` + `ShowPrices`/`ShowRelatedSaleNumber`/`ShowContactNumber`/`ShowSignatureFields` — **all resolved live** from the DR branch's current `ReceiptSettings` `Delivery*` fields on every GET.
- `DeliveryReceiptService` (mirrors `ReceiptService`/`ReturnService`): `CreateOrGetForSaleAsync` — load sale + branch-guard (cross-branch → 404); **if a DR already exists for `(TenantId, SaleId)`, return it unchanged (200)** — this is the only one-per-sale enforcement; else validate, resolve items (each `SaleItemId` must be on the sale and `qty ≤ sold`; **non-null empty `Items` → 400**; **duplicate `SaleItemId` → 400**), snapshot lines, single `SaveChanges` (no transaction, no number to allocate). `GetForSaleAsync` / `GetAsync(id)` branch-guarded. `Amount = Money.Round(qty * unitPrice)`; `!DeliveryShowPrices` ⇒ item `UnitPrice` **and** `Amount` are `null`. Line order `OrderBy(CreatedAtUtc).ThenBy(Id)` for a stable reprint.

**API:** `SalesController` — `GET /api/sales/{id}/delivery-receipt` (200 `DeliveryReceiptDto` | 404), `POST` (201 + `Location: /api/delivery-receipts/{id}` on create, 200 on already-exists). New `DeliveryReceiptsController` — `GET /api/delivery-receipts/{id}` (200 | 404; cross-branch → 404). Both gated by `SalesView`; branch scope enforced in the service.

**Frontend:** `api/deliveryReceipts.ts` (`.getForSale` 404→null, `.createForSale`, `.get`), `api/types.ts` (3 interfaces, no `number`). `components/receipt/deliveryReceiptStyles.ts` — A4 CSS (`@page A4`, ruled signature lines). `pages/DeliveryReceiptPage.tsx` (`/delivery-receipts/:id`, `?print=1` auto-print) — `Recipient:` + `Address:` always; `Contact:` / `Related Sale:` gated; items header `Qty | Product | Unit Price | Amount` with the price columns gated on `showPrices`; stacked signature block; null header ⇒ business block + `DELIVERY RECEIPT` title, custom header ⇒ just the custom text; null footer renders nothing. `components/sales/CreateDeliveryReceiptModal.tsx` + a `SaleDetailPage` button — **"Print delivery receipt"** (no DR yet, status ∈ {Completed, PartiallyRefunded, Refunded}) → modal; **"View delivery receipt"** (DR exists) → opens the print page; no button on a voided sale that has no DR (a DR created before the void still shows "View" — see follow-ups).

## Key contracts / rules (do not break)

- **Fallback is mandatory.** A tenant with zero `ReceiptSettings` rows prints exactly as before (`HardcodedDefault`).
- **Whole-row snapshot** for `ReceiptSettings` — no nullable/tri-state toggles; `PUT` carries all 14 fields; "Reset" = `DELETE` the branch row.
- **DeliveryReceipt: identity + transactional data frozen at creation** (`Id`, linked Sale, recipient, address/contact, notes, prepared-by, snapshotted item name/qty/price). **Presentation is NOT frozen** — header/footer/toggles/width resolve from current `ReceiptSettings` on every reprint. Do **not** add a presentation snapshot to the schema.
- **No DeliveryReceipt document number** anywhere (schema, DTO, UI). Printed reference = creation date + related Sale #.
- **One DR per sale** enforced only in `DeliveryReceiptService` (no DB unique index). Concurrent first-POSTs could double-insert — accepted v1 (UI serialises via the button).
- Delivery receipt access = any `SalesView` role; branch scope enforced server-side (cross-branch GET → 404).
- **A DR can only be *created* for a sale in `Completed` / `PartiallyRefunded` / `Refunded`** — the service rejects a `Voided` sale with 400 (`DELIVERY_RECEIPT_NOT_ALLOWED`), mirroring `ReturnService`. An already-created DR stays retrievable even if its sale is voided afterwards.

## Open follow-ups for the user (non-blocking — from the final whole-branch review)

- **DR reprint carries no void marking.** If a sale is voided *after* its DR exists, the Sale-detail page still shows "View delivery receipt" and the printed DR looks normal. The sales receipt shows a `VOID — SALE CANCELLED` banner in the same situation; the DR has no equivalent. Data is correct (the DR is immutable and unaffected) — this is only about what the reprint says. Fix would add `saleStatus` to `DeliveryReceiptDto` + a banner.
- **Printed line order is arbitrary vs the sale.** `DeliveryReceiptItem`s added in one loop share a `CreatedAtUtc` (Windows `DateTime.UtcNow` ~15 ms resolution), so ordering falls to `ThenBy(Id)` on random Guids. Reprints are *stable* (Ids are persisted) but a multi-line DR prints its lines shuffled. Fix needs a `LineNumber`/`SortOrder` column on `DeliveryReceiptItem` (schema change). `ReceiptService` has the same latent tie on sale lines — pre-existing, not introduced here.
- **`CreateDeliveryReceiptModal` button keys on `deliveryReceiptQuery.data`, not `.isSuccess`.** If that query is slow or errors, the button shows "Print delivery receipt" even when a DR exists; the user can fill the form, submit, and the server correctly returns the *existing* DR (200) — but the modal treats 200/201 alike, closes, and opens a DR showing a different recipient than the one just typed, with no message. Fix: gate the button on `isSuccess`; have `createForSale` surface the status so `onSuccess` can toast "already exists — opening it" on a 200.
- Minor cosmetics: `DeliveryReceiptPage` "Back" calls `navigate(-1)` on a page usually opened via `window.open(_blank)` (no history) — a `window.close()` when `window.opener` is set would be better; `key={i}` on the items rows (static list, harmless). Both copied from `ReceiptPage`.
- Plan A deferred polish still open: `ReceiptSettingsForm` ~470 lines; the sales-preview mock numbers are internally inconsistent; the legacy `.Tenant` vs `.TenantDb` migration-namespace split (10 legacy files — this feature standardised on `.TenantDb`).

_(The final review also flagged: a missing `.dr .bold` CSS rule, an unreachable empty-set guard in `ResolveItems`, and the missing voided-sale server guard — all **fixed** in commit `925ddf1`.)_

## Next steps

1. **`master` is 36 commits ahead of `origin/master` and unpushed** — push when ready (`git push origin master`).
2. Optionally pick up the open follow-ups above (void banner on the DR reprint; `LineNumber` column for stable print order; modal `isSuccess` gating).
3. Phase D items from the original spec remain deferred (no Customer entity, no delivery tracking/status, no digital signature — signature block is blank ruled lines by design).

## Dev environment

- API `http://localhost:5170` (`dotnet run --project src/Negosio.Api`). Frontend `http://localhost:5173` (`npm run dev` from `web/negosio-web`).
- Integration tests spin up their own per-run tenant DBs; the full suite is ~14 min.

---

## Prior session (historical context) — Reports module

The `master` branch this feature is built on already contains the **Reports module Phase B** (Overview KPIs / sales trend / payment breakdown / top products / category performance at `/reports`) merged as `70abed7`, plus `SaleReturn.ApprovedByUserId`. Reports **Phase C** (Branch / Register / Cashier performance) was planned but not built. Reports timezone handling is a fixed Asia/Manila (UTC+8) v1 simplification.
