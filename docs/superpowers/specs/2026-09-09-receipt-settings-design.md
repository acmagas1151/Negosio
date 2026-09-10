# Receipt Settings & Delivery Receipt — Design

_Branch: to be created (`feature/receipt-settings`). Depends on `master` @ `70abed7`._

Status: **APPROVED 2026-09-09** with clarifications folded in below (D1 = whole-row snapshot
resolution, D9 = one-DR-per-sale enforced in the service only). Next step: implementation plan.

---

## 1. Goal

Let authorized users configure how Negosio receipts print — a configurable header, footer, and
business-information block — and add a second, persistent document type: the **Delivery Receipt**.
All authoritative values stay server-side; the front end never recomputes totals or invents data.

Scope is deliberately bounded: this is **not** a delivery-management / fulfilment / customer-master
system. A Delivery Receipt is a printable document generated from an existing Sale plus a small
amount of manually-entered, persisted delivery metadata.

## 2. What exists today (inspection summary)

| Area | Current state |
|---|---|
| Sales receipt (backend) | `ReceiptService` → `ReceiptDto` from persisted `Sale`/`SaleItem`/`Payment`. `GET /api/sales/{id}/receipt`, policy `SalesView`. Branch-scoped (404 cross-branch). |
| Sales receipt (frontend) | `ReceiptPage.tsx`, one component. Header hardcoded `<h1>{storeName}</h1>` + branch line. Footer hardcoded `Thank you!`. Inline CSS, `width:80mm`, monospace, `@media print` + `@page{margin:4mm}`. Printed via `window.print()`. Three entry points, all → `/sales/{id}/receipt`. |
| Settings (backend) | Tax only. `TenantSettingsService`, `GET/PUT /api/settings/tax`, policy `TenantSettingsWrite` = **Owner/Admin**. |
| Settings (frontend) | `SettingsPage.tsx` = one `<TaxSettingsCard>`. **No tabs / sections.** Nav item gated by `settings:write` (Owner/Admin). |
| Branch-specific settings | **Do not exist.** Only per-(tenant,branch) row anywhere is `DocumentNumberCounter` (infra). |
| Manager scope | A Manager is bound to **exactly one** `User.BranchId`. `BranchRoles.IsAllBranch` = Owner/Admin only. "Branches a manager manages" = their one branch. |
| `TenantProfile` | `Name`, `BusinessType`, `TaxRatePercent`, `PricesIncludeTax`. No address / contact / TIN / logo. |
| `Branch` | `Name`, `Code`, `AddressLine1/2`, `City`, `Province`, `PostalCode`, `IsActive`. No contact / TIN. |
| Customer / delivery domain | **Nothing.** No `Customer` entity, no customer name/address/contact on `Sale`, no order/fulfilment/delivery model, no product unit-of-measure. |
| `DocumentNumberService` | Atomic per-(tenant,branch,type) counter, auto-creates row, must run in caller's tx. Enum `Sale=1, Return=2, PurchaseOrder=3, StockTransfer=4`. Format switch per type. |
| Audit infrastructure | No audit-log subsystem. Pattern = `UpdatedByUserId` columns on the entity + `Entity.Touch()` bumping `UpdatedAtUtc`. |
| Migrations | Two contexts. Tenant: `--context TenantDbContext --output-dir Persistence/Migrations/Tenant --namespace Negosio.Infrastructure.Persistence.Migrations.TenantDb`. |

## 3. Decisions taken in this design (see §13 for the rationale / your call)

| # | Decision |
|---|---|
| D1 | **Branch-level settings supported, whole-row snapshot (no field merge).** One tenant-default `ReceiptSettings` row (`BranchId = null`) + optional per-branch override rows. A branch row is a **complete** settings snapshot. Resolution: branch row if present → else tenant-default row → else `HARDCODED_DEFAULT`. Creating a branch override the first time **copies the currently-effective settings** into the new row. "Reset to tenant defaults" = delete the branch row. **No nullable / tri-state booleans** — every toggle is a plain `bool`. Manager is restricted server-side to their own branch's row (may read, not write, the tenant default). |
| D2 | Policy `ReceiptSettingsManage` = **Owner + Admin + Manager** (Admin included for consistency with every other management policy — _confirm_; your brief said Owner+Manager). Manager writes are branch-scoped in the service via `IBranchAccessResolver`. |
| D3 | Business-identity fields live on the entities that own them: add `ContactNumber`, `TaxId` to `TenantProfile`; add `ContactNumber` to `Branch`. `ReceiptSettings` holds only header/footer text + display toggles + width. Receipts read identity from Profile/Branch. |
| D4 | **No `Customer` entity.** Delivery recipient / address / contact are free-text fields persisted on the `DeliveryReceipt` row. |
| D5 | Signature block: `PreparedByUserId` is captured (the logged-in creator). "Delivered by" / "Received by" / "date/time received" print as **blank ruled lines** for hand-completion — not captured digitally in v1. |
| D6 | `SettingsPage` gains a lightweight **tab shell** (`Tax` · `Receipts`). Receipt Settings is its own route `/settings/receipts` with sub-tabs `General` · `Sales receipt` · `Delivery receipt`. |
| D7 | **Receipt width setting included**: `ReceiptWidth` enum `Mm80` (default) · `Mm58`, applied to the thermal Sales receipt only. |
| D8 | `ReceiptDto` extended: per-payment `ReferenceNumber`, `ReceivedAmount`, `ChangeAmount` (from the existing `Payment` columns, not currently surfaced). |
| D9 | **Delivery Receipt is a persistent aggregate** (`DeliveryReceipt` + `DeliveryReceiptItem`), created from a Sale on the Sale-detail page, line items snapshotted. Reprint returns the same document (looked up by `Id`). **One DR per Sale is enforced in the service layer only** — no DB unique constraint on `SaleId`, so multiple/partial deliveries per sale stay possible in a later phase. No POS-modal button. **DR numbering removed 2026-09-10** — no `DR-000001` sequence; the DR is identified by `Id` + creation date + related Sale #. |

## 4. Data model

### 4.1 `ReceiptSettings` (new, tenant DB)

```csharp
public class ReceiptSettings : Entity            // Id, CreatedAtUtc, UpdatedAtUtc, Touch()
{
    public Guid TenantId { get; private set; }
    public Guid? BranchId { get; private set; }   // null = tenant default

    // Shared
    public ReceiptWidth Width { get; private set; }              // Mm80 | Mm58   (D7)

    // Sales receipt
    public string? SalesHeaderText { get; private set; }         // max 500, whitespace-normalized
    public string? SalesFooterText { get; private set; }         // max 500
    public bool SalesShowBranch { get; private set; }
    public bool SalesShowCashier { get; private set; }
    public bool SalesShowPaymentMethod { get; private set; }
    public bool SalesShowTaxLine { get; private set; }
    public bool SalesShowReferenceNumber { get; private set; }

    // Delivery receipt
    public string? DeliveryHeaderText { get; private set; }      // max 500
    public string? DeliveryFooterText { get; private set; }      // max 500
    public bool DeliveryShowPrices { get; private set; }
    public bool DeliveryShowRelatedSaleNumber { get; private set; }
    public bool DeliveryShowContactNumber { get; private set; }
    public bool DeliveryShowSignatureFields { get; private set; }

    public Guid UpdatedByUserId { get; private set; }            // audit (D5-style pattern)

    public static ReceiptSettings CreateDefault(Guid tenantId, Guid? branchId, Guid userId);
    public void Update(ReceiptSettingsValues values, Guid userId);   // validates + Touch()
}

public enum ReceiptWidth { Mm80 = 0, Mm58 = 1 }
```

- Every `bool` is non-nullable (D1) — a row always carries a full, self-contained snapshot.
- EF config `ReceiptSettingsConfiguration` → table `ReceiptSettings`. `Id` `ValueGeneratedNever`.
  Text columns `HasMaxLength(500)`. Filtered unique index: one on `(TenantId)` where
  `BranchId IS NULL` (single tenant-default row) and one on `(TenantId, BranchId)` where
  `BranchId IS NOT NULL` (one row per branch). Insert paths are also service-guarded.
- Not a rowversion concern — last-write-wins on a settings row is acceptable (unlike Sale).
- `Update(...)` overwrites **all** presentation fields from the request every time (no partial
  patch) — the row is always a complete snapshot.

### 4.2 `DeliveryReceipt` + `DeliveryReceiptItem` (new, tenant DB)

```csharp
public class DeliveryReceipt : Entity                   // Id = the identifier (no human-readable number in v1)
{
    public Guid TenantId { get; private set; }
    public Guid BranchId { get; private set; }
    public Guid? SaleId { get; private set; }           // optional link
    public string? RelatedSaleNumber { get; private set; }   // snapshot for display if SaleId set

    public string RecipientName { get; private set; }      // required, ≤ 120
    public string DeliveryAddress { get; private set; }    // required, ≤ 300  — UI label: "Recipient address"
    public string? ContactNumber { get; private set; }     // optional, ≤ 40
    public string? DeliveryNotes { get; private set; }     // optional, ≤ 1000

    public Guid PreparedByUserId { get; private set; }
    public string PreparedByNameSnapshot { get; private set; }

    private readonly List<DeliveryReceiptItem> _items = new();
    public IReadOnlyCollection<DeliveryReceiptItem> Items => _items.AsReadOnly();

    public static DeliveryReceipt Create(...);          // no number — Id is the identifier
}

public class DeliveryReceiptItem : Entity
{
    public Guid TenantId { get; private set; }
    public Guid DeliveryReceiptId { get; private set; }
    public string ProductNameSnapshot { get; private set; }
    public string? VariantNameSnapshot { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal? UnitPrice { get; private set; }     // nullable — printed only if DeliveryShowPrices; column header is "Unit Price"
}
```

- `RecipientName` and `DeliveryAddress` are **required** and **always print** — no per-field
  show/hide toggle for them in v1. `DeliveryReceipt.Create` rejects blank/whitespace for either.
  The UI labels `DeliveryAddress` as **"Recipient address"** (creation modal, print page, preview,
  validation messages) — the persisted property name stays `DeliveryAddress`; do NOT add a second
  `RecipientAddress` property.
- Line items **snapshotted** at creation (same rationale as `SaleItem` — a later rename/reprice
  never changes an issued document). When `SaleId` is provided, items are pre-filled from
  `SaleItem` but still copied.
- `DeliveryReceiptConfiguration`: FK to `Sale` `OnDelete(Restrict)`, items `OnDelete(Cascade)`,
  field-access navigation. **Non-unique** index `(TenantId, SaleId)` for lookup only. No `Number`
  column, no `Number` index.
- **v1: one `DeliveryReceipt` per `SaleId`, enforced in `DeliveryReceiptService` — not by a DB
  constraint.** The create endpoint is idempotent: if a DR already exists for that sale it is
  returned (200); otherwise one is created. Leaving the schema free of a `SaleId`-unique
  constraint keeps partial / multiple deliveries per sale open for a later phase without a
  migration. A DR with `SaleId = null` (ad-hoc) is structurally allowed but not exposed in v1's UI.

### 4.3 ~~`DocumentNumberType` extension~~ — REMOVED (user decision 2026-09-10)

DR numbering is out of scope for v1. No `DocumentNumberType.DeliveryReceipt`, no
`DocumentNumberService` change. A `DeliveryReceipt` has no human-readable number; it is identified
by its `Id` (URL / reprint stability) and referenced on the printout by its creation date and the
related Sale #. Adding a number later is a purely additive follow-up.

### 4.4 Identity fields (D3)

- `TenantProfile`: `+ string? ContactNumber` (max 40), `+ string? TaxId` (max 40).
  `ConfigureBusinessInfo(contactNumber, taxId)` method + `Touch()`.
- `Branch`: `+ string? ContactNumber` (max 40). Threaded through `Branch.Create` / `UpdateDetails`
  and the Branch API DTOs/forms (small additive change to the existing Branches module).

## 5. Effective-settings resolution

New `IReceiptSettingsResolver` (scoped, memoized per request), used by `ReceiptService` and
`DeliveryReceiptService`. **Whole-row resolution — the first row that exists wins, no merging:**

```
EffectiveSettings(branchId):
  if branchId != null and (row = ReceiptSettings where TenantId=t and BranchId=branchId) exists → row
  else if (row = ReceiptSettings where TenantId=t and BranchId is null) exists              → row
  else                                                                                       → HARDCODED_DEFAULT
```

`HARDCODED_DEFAULT` is a static in-code `ReceiptSettingsValues` that reproduces today's output
exactly: no custom header (falls back to business-name + branch block), footer `"Thank you!"`,
every `Show*` = true, `Width = Mm80`. A tenant that never opens Receipt Settings sees **zero
change** — fallback requirement met with no data migration and no seed row.

**Creating a branch override** (`PUT …?branchId=X` when no branch row yet): the service first
resolves `EffectiveSettings(X)` (which yields the tenant default or the hardcoded default), then
inserts a branch row initialized from *that*, then applies the request on top. So a fresh branch
override never starts blank.

**Reset to tenant defaults**: `DELETE /api/settings/receipts?branchId=X` removes the branch row;
resolution then falls through to the tenant default (or hardcoded). Owner/Admin, or the Manager of
that branch.

## 6. Backend — services & endpoints

### 6.1 Receipt settings

| Endpoint | Policy | Notes |
|---|---|---|
| `GET /api/settings/receipts?branchId=` | `SalesView` (read is broad) | `branchId` omitted → tenant default (effective). `branchId` set → effective settings for that branch + `scope` (`TenantDefault` \| `Branch`) + `canEdit`. Manager on a branch they don't own → effective view, `canEdit=false`. |
| `PUT /api/settings/receipts?branchId=` | `ReceiptSettingsManage` | Omitted `branchId` → tenant default (**Owner/Admin only**; Manager → 403). `branchId` set → `IBranchAccessResolver.ResolveTargetBranchAsync` (Manager ≠ own branch → `BRANCH_FORBIDDEN`). First write for a branch: row seeded from currently-effective settings, then request applied (whole-row overwrite). Stamps `UpdatedByUserId` + `UpdatedAtUtc`. |
| `DELETE /api/settings/receipts?branchId=` | `ReceiptSettingsManage` | Deletes the branch override row ("reset to tenant defaults"). `branchId` **required** (never deletes the tenant default). Manager ≠ own branch → `BRANCH_FORBIDDEN`. Idempotent (no row → 204). |

`ReceiptSettingsService` + `ReceiptSettingsContracts.cs` (`ReceiptSettingsDto` including
`Scope` (`TenantDefault`/`Branch`), `CanEdit`, `UpdatedAtUtc`, `UpdatedByName`), FluentValidation
`UpdateReceiptSettingsRequestValidator` (lengths ≤ 500 / 40, trims, collapses internal whitespace
runs, strips control chars; **plain text only — no HTML permitted or rendered**).

### 6.2 Delivery receipts

| Endpoint | Policy | Notes |
|---|---|---|
| `POST /api/sales/{saleId}/delivery-receipt` | `SalesView` + service branch check (`ReceiptService` pattern) | Roles: Owner/Admin/Manager/Cashier (same as who can open a sale). Body = delivery metadata (recipient name + recipient address required) + optional item qty overrides. If a DR already exists for this sale → returns it (200). Else snapshots items from the sale, persists, stamps `PreparedByUserId`. Returns 201. |
| `GET /api/sales/{saleId}/delivery-receipt` | `SalesView` | Returns the DR for that sale or 404. |
| `GET /api/delivery-receipts/{id}` | `SalesView` | Branch-scoped like `ReceiptService`. Feeds the print page. |

`DeliveryReceiptService` + contracts + validator (recipient/address required, lengths bounded,
notes ≤ 1000). Branch scoping identical to `ReceiptService` (cross-branch → 404).

### 6.3 `ReceiptDto` changes

```
ReceiptDto  += HeaderText?, FooterText?, BusinessName, BranchName, BranchAddress?,
               BranchContactNumber?, TaxId?, ShowBranch, ShowCashier, ShowPaymentMethod,
               ShowTaxLine, ShowReferenceNumber, Width
ReceiptPaymentDto += ReferenceNumber?, ReceivedAmount?, ChangeAmount?
```

`ReceiptService` joins `TenantProfile` (name/contact/taxId) + `Branch` (address/contact) + the
resolved settings. No behavioural change when settings are absent.

## 7. Frontend

### 7.1 Shared receipt primitives (`web/negosio-web/src/components/receipt/`)

- `ReceiptHeader.tsx` — renders custom header text **or** the business-info block (name / branch /
  address / contact / TIN) per toggles. Plain text, no `dangerouslySetInnerHTML`.
- `ReceiptFooter.tsx` — footer text (multi-line, whitespace-preserved, escaped).
- `ReceiptBusinessInfo.tsx` — the name/address/contact/TIN lines, shared by header + delivery.
- `receiptStyles.ts` — exports the thermal CSS as a function of `width` (`80mm`/`58mm`, font-size
  step) and a separate **A4 stylesheet** for the delivery receipt (margins, signature grid).
- `useReceiptSettings(branchId?)` hook — TanStack query, `['settings','receipts',branchId]`.

### 7.2 `ReceiptPage.tsx` (modified)

Consumes the extended `ReceiptDto`; header/footer via the shared components; width from the DTO;
new optional rows (reference number, tendered/change) gated by the DTO toggles. Void/refund
banners unchanged.

### 7.3 `DeliveryReceiptPage.tsx` (new, route `/delivery-receipts/:id`)

A4/Letter layout: business header + `DELIVERY RECEIPT` title · document details block · items
table · delivery notes · signature block · configurable footer. `?print=1` auto-prints, same
convention as the sales receipt.

**Document details block** — one line each, in this order:

```
Recipient: <RecipientName>
Address:   <DeliveryAddress>              (wraps onto continuation lines)
Contact:   <ContactNumber>               (hidden when DeliveryShowContactNumber = false)
Related Sale: #<RelatedSaleNumber>       (hidden when DeliveryShowRelatedSaleNumber = false)
```

`Recipient` and `Address` are **core fields and always render** — there are no per-field
show/hide toggles for them in v1. Only `Contact` and `Related Sale` are gated. (`DeliveryAddress`
is the authoritative persisted field; its user-facing label is **"Recipient address"** everywhere
in the UI — creation modal, print page, preview, validation messages.)

**Items table** — columns `Qty | Product | Unit Price | Amount`. `Unit Price` + `Amount` render
only when `DeliveryShowPrices` (and the value is non-null). The column header is literally
**"Unit Price"** (was "Unit" in an earlier draft — label only, no pricing-logic change).

**Signature block** — rendered only when `DeliveryShowSignatureFields`. Stacked, not a grid:

```
Prepared by: <PreparedByNameSnapshot>
Delivered by: ____________________
Received by:  ____________________
Date received: __________________
```

`Prepared by` is filled from the persisted `PreparedByNameSnapshot`; the other three are blank
ruled lines for hand-completion (no digital capture — D5).

### 7.4 Settings UI

- `SettingsPage.tsx` → tab shell `Tax` · `Receipts` (or a `/settings` index with sub-routes —
  whichever is smaller given the current single-card page; **D6**).
- `pages/settings/ReceiptSettingsPage.tsx` — title **"Receipt settings"**, subtitle
  _"Customize the information and appearance shown on printed receipts."_ Sub-tabs:
  - **General** — business name (from Profile, read-only link to edit), contact number, TIN,
    branch selector (Owner/Admin: tenant default + each branch; Manager: their branch only),
    receipt width.
  - **Sales receipt** — header textarea · toggles (branch / cashier / payment method / tax /
    reference number) · footer textarea · **live preview** (right pane, client-rendered from
    unsaved form state, clearly labelled "Preview — not yet saved").
  - **Delivery receipt** — header textarea · toggles (prices / related Sale# / contact number /
    signature fields) · footer textarea · live preview.
  - Primary action **"Save changes"**; dirty-tracking + Discard, mirroring `TaxSettingsCard`.
  - When a branch is selected and a branch override exists: a **"Reset to tenant defaults"**
    action (`DELETE …?branchId=`) with a confirm. When none exists, the form shows the effective
    (inherited) values with a "Using tenant defaults" note; the first Save creates the override.
- `nav.ts` — `Settings` group gains a child `Receipt settings` → `/settings/receipts`, capability
  `receipt:settings`. `useCan.ts` — `'receipt:settings'` = `Owner/Admin/Manager` (mirrors D2).
- `SaleDetailPage.tsx` — new action: **"Print delivery receipt"**. If a DR exists → opens
  `/delivery-receipts/{id}?print=1`. If not → opens a modal to enter recipient/address/contact/
  notes (+ per-line qty tweak) → `POST` → opens the print page. After creation the button label
  becomes "View delivery receipt".

## 8. Preview vs. authoritative config

The right-pane preview is rendered **entirely client-side from current form state** and is never
persisted or printed. Printed / reprinted receipts always fetch persisted settings via the
backend DTO. The preview component and the real `ReceiptHeader`/`ReceiptFooter` share the same
rendering code, fed different props (unsaved form values vs. server DTO), so they can't visually
diverge.

## 9. Validation & limits

| Field | Limit |
|---|---|
| Sales/Delivery header & footer text | ≤ 500 chars each, trimmed, internal whitespace runs collapsed to single, control chars stripped, newlines kept (max ~6 lines enforced) |
| Contact number, Tax ID | ≤ 40 chars |
| Recipient name | ≤ 120, required |
| Delivery address | ≤ 300, required |
| Delivery notes | ≤ 1000 |
| All text | **plain text only** — never interpreted as HTML/markdown on print |

Server-side FluentValidation is the source of truth; the forms mirror the limits for UX.

## 10. Security / audit

- `ReceiptSettings.UpdatedByUserId` + `UpdatedAtUtc` — "who last changed receipt settings" is a
  join away. Surfaced in the Settings UI ("Last updated by X on …").
- `DeliveryReceipt.PreparedByUserId` + `CreatedAtUtc`.
- Every write path re-checks role (policy) **and** branch (`IBranchAccessResolver`) server-side.
  Hiding the nav item is cosmetic only — matches the existing Reports approach.

## 11. Migrations

One tenant migration, e.g. `AddReceiptSettingsAndDeliveryReceipts`:
`ReceiptSettings`, `DeliveryReceipts`, `DeliveryReceiptItems` tables; `TenantProfile.ContactNumber`
+ `TenantProfile.TaxId`; `Branch.ContactNumber`. All **additive & nullable** — existing tenant DBs
migrate clean, existing receipts keep printing via `HARDCODED_DEFAULT`.
`--context TenantDbContext --output-dir Persistence/Migrations/Tenant --namespace …Migrations.TenantDb`.
No platform migration.

## 12. Testing (maps to your acceptance list §)

**Unit** — `ReceiptSettingsValues` validation/normalization; `EffectiveSettings` merge precedence
(default ⊕ tenant ⊕ branch); `DocumentNumberService` `DeliveryReceipt` format.

**Integration** — `ReceiptSettingsTests`: Owner edits tenant default (1); Owner edits a branch
override (1); Manager edits own branch only, 403 on another branch and on tenant default (2);
Cashier/Viewer PUT → 403 (3); header/footer persist & round-trip after re-auth (4,5,6);
oversized/HTML input rejected (9,VALIDATION). `ReceiptDtoTests`: default settings ⇒ byte-identical
to pre-change output (8,10,12); custom header/footer applied (7); real persisted totals & payment
values unchanged (11); reprint reflects current saved settings (13).
`DeliveryReceiptTests`: create from sale ⇒ persisted (14; ~~15 "unique DR number" — removed~~); recipient/
address/items correct & snapshotted (16,17); price columns obey `DeliveryShowPrices` (18);
header/footer applied (19); second `POST` for the same sale ⇒ same DR id & number, no new
document (20).

**Frontend** — `tsc --noEmit` + `npm run build` clean; preview renders from unsaved state; Manager
sees only their branch in the selector; Cashier never sees the nav item and `/settings/receipts`
shows the access-denied page.

**Live verification** (headless Chrome, real API + Vite) before declaring done — same bar as Reports.

## 13. Decisions — RESOLVED (2026-09-09)

| # | Outcome |
|---|---|
| D1 | Approved. **Whole-row snapshot**, no field merge, no tri-state booleans. branch row → tenant default → hardcoded. First branch override seeded from effective settings. Reset = `DELETE` the branch row. |
| D2 | Approved. `ReceiptSettingsManage` = Owner + Admin + Manager. Manager strictly branch-scoped server-side. Cashier/others cannot modify. |
| D3 | Approved. `TenantProfile.ContactNumber`, `TenantProfile.TaxId`, `Branch.ContactNumber`. `ReceiptSettings` = presentation/config only. |
| D4 | Approved. No `Customer` entity. `DeliveryReceipt` persists recipient/address/contact/notes snapshots. Future Customer domain can associate later without losing snapshots. |
| D5 | Approved. `PreparedByUserId` persisted; delivered-by / received-by / signature / date-received are blank ruled lines. No PoD / digital signature. |
| D6 | Approved. Settings tabs `Tax` · `Receipts`; Receipt Settings sub-tabs `General` · `Sales receipt` · `Delivery receipt`; proper routes where they fit the router cleanly. |
| D7 | Approved. Sales receipt `80mm` default / `58mm` compact. Delivery Receipt A4/Letter, unaffected. |
| D8 | Approved. `ReceiptDto`/`ReceiptPaymentDto` gain persisted `ReferenceNumber` / `ReceivedAmount` / `ChangeAmount`; frontend renders, never recalculates. |
| D9 | Approved. Persistent `DeliveryReceipt` aggregate, create from Sale Detail. Reprint = same persisted DR (by `Id`). One DR per sale **enforced in service only** — schema/domain stay open to multiple/partial deliveries later. No POS button. **DR numbering removed 2026-09-10** — no `DR-000001`, no `DocumentNumberService`; the DR is identified by `Id` + creation date + related Sale #. |
| — | **2026-09-10:** delivery item column label is **"Unit Price"** (not "Unit"). `DeliveryAddress` (required, ≤ 300) is surfaced everywhere as **"Recipient address"** — label only, no second property. Recipient name + recipient address are required. Print/preview: `Recipient:` + `Address:` always show; `Contact:` + `Related Sale:` gated. Signature block stacked (Prepared by / Delivered by / Received by / Date received). |

Cross-cutting confirmations: existing tenants print unchanged via fallback (address/TIN appear only
when set — 2026-09-10, kept as-is); plain text only (no
arbitrary HTML); preview may use unsaved local form state; real print/reprint always uses
persisted settings; settings writes persist `UpdatedByUserId`/`UpdatedAtUtc`; delivery item
names/prices/quantities are snapshots; no Customer module; no delivery tracking/status; no digital
signatures; no POS button.

---

_Next step: `superpowers:writing-plans` → a task-by-task implementation plan. Do not merge or push._
