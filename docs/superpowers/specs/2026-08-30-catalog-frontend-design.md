# Catalog Frontend — Design

> Sub-project 1 of the Negosio web frontend catch-up (frontend is two backend
> phases behind). Scope: shared frontend **foundation** + **Catalog UI**
> (categories, products, product variants). Later sub-projects — in dependency
> order — are Inventory + Tax settings, Registers/Sessions, POS checkout,
> Sales/Returns.

- **Date:** 2026-08-30
- **Branch target:** `master` (feature branch during build)
- **Stack (unchanged):** React 19, TypeScript (strict), Vite 8, React Router 7
  (`BrowserRouter` + `Routes`/`Route`), TanStack Query v5, Tailwind v4 design
  tokens, lucide-react, `oxlint`. **No new npm dependencies.**
- **Backend:** already shipped and unchanged. This is a frontend-only
  sub-project. Every API contract below already exists.

---

## 1. Goals / Non-goals

### Goals

1. A user can manage their product **categories**: list, search, filter by
   status, create, edit, deactivate, reactivate.
2. A user can manage **products**: a searchable / filterable / sortable /
   paginated list; create a *simple* product or a product *with variants*;
   view a product's detail; edit a product; deactivate / reactivate.
3. A user can manage a product's **variants** from the product detail page:
   list, add, edit, deactivate.
4. The UI mirrors the backend's role rules as **UX affordances only**
   (`useCan`): write actions hidden without `catalog:write`; cost figures shown
   only when the API actually returns them.
5. Reusable frontend foundation established for the later slices: per-area API
   modules, `Table`, `Pagination`, `SearchInput`, `Modal`, `ConfirmDialog`,
   `usePagedQuery`, `useCan`.

### Non-goals

- Inventory, tax settings, registers, POS, sales, returns UI (later
  sub-projects).
- Staff / user management, invitations, role assignment UI.
- Bulk import/export, product images, multi-currency.
- Any backend change. If a genuine contract gap appears, stop and report it —
  do not work around it.
- A generic form/table abstraction framework. Build only what Catalog needs.

---

## 2. Backend contract reference (already implemented)

### Categories — `/api/categories`

| Method | Path | Body | Response | Notes |
| --- | --- | --- | --- | --- |
| GET | `/api/categories` | — (query: `search`, `isActive`, `page`, `pageSize`) | `PagedResult<CategoryDto>` | reads open to any tenant user |
| GET | `/api/categories/{id}` | — | `CategoryDto` | |
| POST | `/api/categories` | `CreateCategoryRequest` | `201 CategoryDto` | policy `CatalogWrite` |
| PUT | `/api/categories/{id}` | `UpdateCategoryRequest` | `CategoryDto` | policy `CatalogWrite`; carries `isActive` → used for reactivate |
| DELETE | `/api/categories/{id}` | — | `204` | policy `CatalogWrite`; **soft** — deactivates |

```
CategoryDto      { id, name, description?: string|null, isActive, productCount, createdAtUtc, updatedAtUtc }
CreateCategoryRequest { name, description?: string|null }
UpdateCategoryRequest { name, description?: string|null, isActive }
```

### Products — `/api/products`

| Method | Path | Body | Response | Notes |
| --- | --- | --- | --- | --- |
| GET | `/api/products` | — (query below) | `PagedResult<ProductDto>` | reads open; cost fields nulled for redacted roles |
| GET | `/api/products/{id}` | — | `ProductDetailDto` | `{ product, variants[] }` |
| POST | `/api/products` | `CreateProductRequest` | `201 ProductDetailDto` | policy `CatalogWrite` |
| PUT | `/api/products/{id}` | `UpdateProductRequest` | `ProductDetailDto` | policy `CatalogWrite`; **simple fields only, no variants** |
| DELETE | `/api/products/{id}` | — | `204` | policy `CatalogWrite`; **soft** — deactivates |

List query params: `search` (matches product name, or variant SKU/barcode
`Contains`), `categoryId`, `isActive` (bool), `trackInventory` (bool), `page`,
`pageSize`, `sortBy` ∈ {`name` (default), `sellingprice`, `createdatutc`},
`sortDirection` ∈ {`asc` (default), `desc`}.

```
ProductVariantDto { id, name, isDefault, sku?, barcode?, costPrice?: number|null, sellingPrice, isActive, createdAtUtc, updatedAtUtc }
ProductDto {
  id, categoryId, categoryName, name, description?: string|null,
  trackInventory, isActive, hasVariants, variantCount,
  sku?: string|null, barcode?: string|null,             // simple products only; null when hasVariants
  minCostPrice?: number|null, maxCostPrice?: number|null, // null when redacted OR no active variant
  minSellingPrice: number, maxSellingPrice: number,
  createdAtUtc, updatedAtUtc
}
ProductDetailDto { product: ProductDto, variants: ProductVariantDto[] }
VariantInput { name, sku?: string|null, barcode?: string|null, costPrice: number, sellingPrice: number }
CreateProductRequest {
  categoryId, name, description?: string|null, trackInventory,
  sku?: string|null, barcode?: string|null, costPrice: number, sellingPrice: number, // simple path
  variants?: VariantInput[] | null                                                   // variant path
}
UpdateProductRequest {
  categoryId, name, description?: string|null, trackInventory, isActive,
  sku?: string|null, barcode?: string|null, costPrice: number, sellingPrice: number
}
```

Backend behaviour worth noting for the UI:
- Every product owns ≥ 1 variant. A *simple* product has one hidden **default**
  variant; adding the first real variant **promotes** it (`hasVariants`
  flips true; the product-level `sku`/`barcode`/prices become `null`).
- `CreateProductRequest` with a non-empty `variants[]` → variant product; else
  the `sku`/`costPrice`/`sellingPrice` fields → simple product.
- `UpdateProductRequest` only edits the product + (for simple products) its
  default variant. Variant products manage pricing per-variant.

**Verified — `UpdateProductRequest` on a variant product**
([`ProductService.UpdateAsync`](../../../src/Negosio.Application/Catalog/ProductService.cs) L138–170):
the `sku` / `barcode` / `costPrice` / `sellingPrice` fields are used **only**
inside `if (!product.HasVariants) { … UpdateDefaultVariant(…) }`. For a
`hasVariants` product that whole block is skipped — those four fields **cannot
mutate any data**. `UpdateProductRequestValidator` only requires
`costPrice >= 0` and `sellingPrice >= 0` (both accept `0`) and `sku`/`barcode`
≤ 64 chars or null. So for a variant product it is safe (and validated) to send
`costPrice: 0, sellingPrice: 0, sku: null, barcode: null`. This is a
**confirmed** fact, not an assumption — it is not a plan-time gate. Re-confirm
with one line of reading if `ProductService` changes before the build.

### Product variants — `/api/products/{productId}/variants`

| Method | Path | Body | Response | Notes |
| --- | --- | --- | --- | --- |
| GET | `.../variants` | — | `ProductVariantDto[]` | |
| POST | `.../variants` | `VariantInput` | `201 ProductVariantDto` | policy `CatalogWrite` |
| PUT | `.../variants/{variantId}` | `VariantInput` | `ProductVariantDto` | policy `CatalogWrite` |
| DELETE | `.../variants/{variantId}` | — | `204` | policy `CatalogWrite`; **soft** — deactivates |

### Pagination envelope

```
PagedResult<T> { items: T[], page, pageSize, totalCount, totalPages }
```
`pageSize` default 20, max 100 (server clamps).

### Error envelope (already handled by `ApiError`)

`{ code, message, traceId, errors?: Record<string, string[]> }`. Relevant
`code`s: `CATEGORY_ALREADY_EXISTS`, `CATEGORY_NOT_FOUND`, `PRODUCT_NOT_FOUND`,
`SKU_ALREADY_EXISTS`, `BARCODE_ALREADY_EXISTS`, `VARIANT_NOT_FOUND`,
`VARIANT_REQUIRED`, `PRODUCT_HAS_VARIANTS`, `VALIDATION_FAILED` (400 with
`errors`).

### Role rules (from `CatalogAccess`, mirrored client-side by `useCan`)

- `catalog:write` → **Owner, Admin, Manager**
- `costs:view` → **Owner, Admin, Manager, InventoryStaff**
- All other roles (Cashier, KitchenStaff, Viewer): read-only catalog, selling
  price only.

**The backend is authoritative for cost redaction.** `useCan('costs:view')` is
a UX affordance only — it decides whether to *render a Cost column/field at
all*. Independently, **every** read of `minCostPrice` / `maxCostPrice` /
`variant.costPrice` must be null-checked before display, because the API nulls
those fields for redacted roles regardless of what the client believes. Never
compute or back-fill a cost from the selling price.

---

## 3. Foundation

### 3.1 API modules — `src/api/catalog.ts`

Follows the existing `authApi` / `dashboardApi` shape (thin wrappers over
`apiRequest`, typed).

```ts
export const categoriesApi = {
  list:       (q: CategoryListParams) => apiRequest<PagedResult<CategoryDto>>(`/api/categories${qs(q)}`),
  get:        (id: string)            => apiRequest<CategoryDto>(`/api/categories/${id}`),
  create:     (body: CreateCategoryRequest) => apiRequest<CategoryDto>('/api/categories', { method: 'POST', body }),
  update:     (id: string, body: UpdateCategoryRequest) => apiRequest<CategoryDto>(`/api/categories/${id}`, { method: 'PUT', body }),
  deactivate: (id: string)           => apiRequest<void>(`/api/categories/${id}`, { method: 'DELETE' }),
}

export const productsApi = {
  list:       (q: ProductListParams) => apiRequest<PagedResult<ProductDto>>(`/api/products${qs(q)}`),
  get:        (id: string)            => apiRequest<ProductDetailDto>(`/api/products/${id}`),
  create:     (body: CreateProductRequest) => apiRequest<ProductDetailDto>('/api/products', { method: 'POST', body }),
  update:     (id: string, body: UpdateProductRequest) => apiRequest<ProductDetailDto>(`/api/products/${id}`, { method: 'PUT', body }),
  deactivate: (id: string)           => apiRequest<void>(`/api/products/${id}`, { method: 'DELETE' }),
}

export const variantsApi = {
  list:       (productId: string) => apiRequest<ProductVariantDto[]>(`/api/products/${productId}/variants`),
  create:     (productId: string, body: VariantInput) => apiRequest<ProductVariantDto>(`/api/products/${productId}/variants`, { method: 'POST', body }),
  update:     (productId: string, variantId: string, body: VariantInput) => apiRequest<ProductVariantDto>(`/api/products/${productId}/variants/${variantId}`, { method: 'PUT', body }),
  deactivate: (productId: string, variantId: string) => apiRequest<void>(`/api/products/${productId}/variants/${variantId}`, { method: 'DELETE' }),
}
```

`qs()` — a tiny local helper that serialises a params object to a query string,
dropping `undefined` / `null` / `''`. Lives in `src/api/query-string.ts`.

### 3.2 Types — `src/api/types.ts`

Add: `PagedResult<T>`, `CategoryDto`, `CreateCategoryRequest`,
`UpdateCategoryRequest`, `ProductVariantDto`, `ProductDto`, `ProductDetailDto`,
`VariantInput`, `CreateProductRequest`, `UpdateProductRequest`, and the
`*ListParams` param types. Also correct the stale `DashboardResponse` to match
what the API returns today (adds `totalProducts`, `activeCategories`,
`lowStockItems`, `todaysSales`, `todaysTransactions`, `averageTransactionValue`)
— the Dashboard page keeps rendering only its current Phase-1 cards; this is
just type hygiene so the new types file is correct.

### 3.3 Hooks — `src/hooks/usePagedQuery.ts`

Owns list-page state and keeps it in the URL so a list view is linkable and
survives refresh / back-button.

```ts
usePagedQuery<TFilters extends Record<string, string | undefined>>(opts: {
  defaultPageSize?: number      // 20
  defaultFilters: TFilters      // e.g. { categoryId: undefined, status: undefined }
}) => {
  page: number
  pageSize: number
  search: string                // debounced value used for queries
  searchInput: string           // raw input value
  setSearchInput: (v: string) => void
  filters: TFilters
  setFilter: (key: keyof TFilters, value: string | undefined) => void
  sortBy?: string
  sortDirection?: 'asc' | 'desc'
  setSort: (col: string) => void   // toggles asc/desc/clear on repeated calls
  setPage: (p: number) => void
}
```

- Backed by `useSearchParams` (React Router). Keys: `page`, `q`, `sort`, `dir`,
  plus one key per filter.
- `search` is `searchInput` **debounced 350 ms** (`useDebouncedValue` helper).
- **Any change to `search` or a filter or sort resets `page` to 1.**
- `pageSize` is fixed per page for now (not user-controlled); the option exists
  so a later slice can expose it.
- Not generic beyond string params — numbers/bools are passed as strings and
  coerced at the call site.

### 3.4 `src/lib/useCan.ts`

```ts
type Capability = 'catalog:write' | 'costs:view'
export function useCan(cap: Capability): boolean
```
Pure function of `useAuth().user?.role`. Role sets duplicated from
`CatalogAccess` with a comment pointing back to it. Returns `false` when no
user.

### 3.5 Shared UI primitives — `src/components/ui/`

All added to the `index.ts` barrel. Styling uses the existing Tailwind tokens
(`text-text-*`, `border-border*`, `bg-surface*`, `primary-*`, `danger`) and
`cn`.

| Component | Props (essence) | Behaviour |
| --- | --- | --- |
| `Table` | `<Table>`, `Table.Head`, `Table.Row`, `Table.Cell`, `Table.HeaderCell` (with optional `sort` + `onSort` for sortable columns) | Semantic `<table>`; horizontal scroll wrapper; renders `EmptyState` / skeleton rows via caller. Sort header shows an up/down chevron. |
| `Pagination` | `page`, `totalPages`, `totalCount`, `onPageChange` | "1–20 of 84" + Prev/Next buttons; hidden when `totalPages <= 1`. |
| `SearchInput` | `value`, `onChange`, `placeholder` | Text input with a search icon + a clear (`x`) button. Debouncing lives in `usePagedQuery`, not here. |
| `Modal` | `open`, `onClose`, `title`, `children`, `footer?` | Portal to `document.body`; backdrop click + `Esc` close; focus trapped; `role="dialog"` `aria-modal`; body scroll locked while open; returns focus to opener. |
| `ConfirmDialog` | `open`, `onClose`, `onConfirm`, `title`, `message`, `confirmLabel`, `tone?: 'default' \| 'danger'`, `loading?` | Thin `Modal` wrapper. Confirm button shows `loading` spinner; disabled while pending. |

### 3.6 Routing & navigation

`src/App.tsx` — add, all inside `ProtectedRoute`:

```
/categories            CategoriesPage
/products              ProductsPage
/products/new          ProductCreatePage
/products/:id          ProductDetailPage
/products/:id/edit     ProductEditPage
```

`src/lib/nav.ts` — introduce a grouped nav model:

```ts
interface NavGroup { label?: string; items: NavItem[] }
```

- `Dashboard` (ungrouped, top)
- **Catalog** group → `Products` (`/products`), `Categories` (`/categories`)
- `Inventory`, `POS`, `Sales`, `Reports`, `Staff`, `Settings` → remain
  `enabled: false` "Soon" (ungrouped or in a second "Coming soon" implied
  block — keep the existing flat "Soon" rendering).

`src/components/layout/Sidebar.tsx` — render an optional small uppercase group
label above a group's items; the disabled "Soon" rows render exactly as today.
`Products` / `Categories` become real `NavLink`s with the active-state
treatment already in the file.

`ProductsPage` and `CategoriesPage` should mark **both** Catalog nav items as
appropriate — a product detail/edit route still highlights `Products`
(`NavLink` `to="/products"` with `end={false}`).

**Sidebar footer:** replace the literal `Phase 1 · SaaS foundation` with a
neutral product footer — `Negosio` + the app version (`v{__APP_VERSION__}` wired
from `package.json` via Vite `define`, or simply drop the line). No
roadmap/phase language in the product UI.

---

## 4. Catalog pages

Every page is wrapped in `<DashboardLayout title=...>`. Loading → skeleton
rows / `LoadingState`; error → `ErrorState` with retry; empty → `EmptyState`.
Mutations use `useMutation`; on success invalidate the relevant query keys and
fire a `useToast()` success; on `ApiError` map field errors or toast the
message (see §5).

Query keys:
- `['categories', params]`
- `['categories', 'all-active']` — the unpaginated active-category list used to
  populate the product category `<Select>`. Fetched with
  `list({ isActive: true, pageSize: ACTIVE_CATEGORY_FETCH_LIMIT })` where
  `ACTIVE_CATEGORY_FETCH_LIMIT = 100` is a **named constant** in
  `src/api/catalog.ts` with a comment explaining it is a stopgap until a
  typeahead/lookup endpoint exists. Acceptable for Phase 3 tenant sizes.
- `['products', params]`
- `['product', id]`

**All mutation-triggering controls** (buttons, menu items, confirm buttons) are
`disabled` while their `useMutation` is `isPending`, and the confirm/submit
button shows a spinner. This applies to every create / edit / deactivate /
reactivate across categories, products and variants.

### 4.1 Categories — `CategoriesPage` (`/categories`)

- **Header:** `PageHeader` "Categories" + `New category` button (only if
  `useCan('catalog:write')`).
- **Toolbar:** `SearchInput` (placeholder "Search categories"); Status
  `Select` (All / Active / Inactive) → `isActive` param.
- **`Table`:** columns
  - *Name* (semibold)
  - *Description* — muted, `line-clamp-1`; em dash when null
  - *Products* — `productCount`, right-aligned
  - *Status* — `Badge` (`success` Active / `neutral` Inactive)
  - *Actions* (only if `catalog:write`) — `Edit`; `Deactivate` (active rows) or
    `Reactivate` (inactive rows)
- **`Pagination`** below.
- **Create / Edit:** a single `CategoryFormModal` (`Modal`) with `TextField`
  Name (required, max ~80) + a `<textarea>` Description (optional). Reused for
  create and edit (edit pre-fills; edit also keeps the row's current
  `isActive`). Submit disabled while pending. Server field errors → mapped onto
  the Name field; `CATEGORY_ALREADY_EXISTS` → inline Name error "A category with
  this name already exists."
- **Deactivate:** `ConfirmDialog` (`tone="danger"`, confirm "Deactivate") —
  message notes products keep the category but it is hidden from pickers.
- **Reactivate:** direct `categoriesApi.update(id, { name, description,
  isActive: true })`, no dialog, success toast.

### 4.2 Products list — `ProductsPage` (`/products`)

- **Header:** `PageHeader` "Products" + `New product` button (gated) →
  `/products/new`.
- **Toolbar (wraps on small screens):**
  - `SearchInput` — placeholder "Search name, SKU or barcode"
  - **Category** `Select` — "All categories" + active categories
  - **Status** `Select` — All / Active / Inactive
  - **Tracking** `Select` — All / Tracked / Not tracked
- **`Table`:** columns
  - *Product* — name (semibold, links to `/products/:id`) + description muted
    `line-clamp-1`. **Sortable** (`name`).
  - *Category* — `categoryName`
  - *Price* — `formatMoney(minSellingPrice)`, or `min–max` when
    `hasVariants && maxSellingPrice !== minSellingPrice`. **Sortable**
    (`sellingprice`).
  - *Cost* — **column only rendered when `useCan('costs:view')`.** Value:
    `minCostPrice == null` → em dash; else single or `min–max` like Price.
    Never computed from selling price.
  - *Variants* — `hasVariants ? variantCount : 'Simple'`
  - *Tracking* — `Warehouse` icon + "Tracked" pill when `trackInventory`, else
    muted em dash
  - *Status* — `Badge` Active/Inactive
- **Only the product name is a link** (`<Link to="/products/:id">`). The table
  row itself is **not** clickable — no row-level `onClick`/navigation. (No
  per-row action menu either; edit / deactivate live on the detail page.)
- **`Pagination`** below. Page size 20.
- Default sort `name asc`. Clicking a sortable header cycles
  asc → desc → (back to default).

### 4.3 Product detail — `ProductDetailPage` (`/products/:id`)

Loads `productsApi.get(id)` → `ProductDetailDto`.

- **Header:** back link to `/products`; product name; `Badge` Active/Inactive.
  Buttons (only if `catalog:write`):
  - `Edit` → `/products/:id/edit`
  - `Deactivate` (active) via `ConfirmDialog`, or `Reactivate` (inactive) —
    both go through `productsApi.update` with a request built by the **single
    shared helper** `buildUpdateProductRequest(product, patch)` (see §5), so a
    variant product has exactly one request-construction path whether it is
    edited or reactivated.
- **Attributes `Card`:** Category, Description (or em dash), Track inventory
  (Yes / No), Created, Last updated.
- **Pricing / Variants `Card`:**
  - **Simple product (`hasVariants === false`):**
    - Read-only rows: SKU, Barcode, Selling price, and — if `costs:view` and
      `product.minCostPrice != null` — Cost price + Margin
      (`(selling - cost) / selling`, shown as `%`, guard divide-by-zero).
    - Sub-text: *"This product is sold as a single item. Add a variant to sell
      multiple versions (sizes, flavours, colours…)."*
    - `Add variant` button (gated) → `VariantFormModal`. On success the product
      query is invalidated; the card re-renders in "has variants" shape.
  - **Has variants (`hasVariants === true`):**
    - `Table` of `variants` (the DTO already includes inactive ones — show all;
      inactive rows muted + Inactive badge, or filter to active with a toggle —
      **default: show active only, with a "Show inactive" checkbox**).
      Columns: *Variant* (name; "Default" badge if `isDefault`), *SKU*,
      *Barcode*, *Selling*, *Cost* + *Margin* (gated / null-guarded), *Status*.
    - Row actions (gated): `Edit` → `VariantFormModal` (pre-filled);
      `Deactivate` → `ConfirmDialog`. The backend rejects deactivating the last
      active variant / the default in some cases → surface the error message in
      a toast, do not pre-guess the rule.
    - `Add variant` button (gated).

`VariantFormModal` — `Modal` with `VariantInput` fields: Name (required), SKU,
Barcode, Cost price (number, ≥ 0; required by contract — default 0), Selling
price (number, ≥ 0). `SKU_ALREADY_EXISTS` / `BARCODE_ALREADY_EXISTS` → inline
errors on the relevant field.

### 4.4 Product create — `ProductCreatePage` (`/products/new`)

Guarded: if `!useCan('catalog:write')` → redirect to `/products` (the button
that leads here is already hidden, this is defence-in-depth).

Single `<form>` (full page, `max-w-2xl`), sections:

1. **Details**
   - Category `Select` — required; options = active categories. If the tenant
     has **no** categories yet, replace the select with an inline notice +
     "Create a category first" link to `/categories`, and disable submit.
   - Name — required, max ~120
   - Description — optional `<textarea>`
   - **Track inventory** — toggle, default **on**. Hint: "Deduct stock on sale
     and show this product in inventory."
2. **Pricing** — a segmented control / toggle: **"Single item"** vs
   **"Has variants"**
   - **Single item** (default):
     - SKU — optional
     - Barcode — optional
     - Cost price — number, ≥ 0, required (default `0`). Shown to everyone on
       the create form (creator is `catalog:write` ⊆ `costs:view`).
     - Selling price — number, ≥ 0, required
   - **Has variants:**
     - Repeatable **variant rows** (min 1; "Add variant" appends; each row has a
       remove button, disabled when only one row): Name (required), SKU,
       Barcode, Cost (≥ 0), Selling (≥ 0).
     - Product-level SKU/barcode/price hidden.
3. **Submit bar:** `Cancel` (→ `/products`) + `Create product`.
   - Builds `CreateProductRequest`: single-item → `variants: null` and the
     flat fields; has-variants → `variants: [...]` (flat price fields sent as
     `0` / null, ignored by the backend when `variants` non-empty).
   - Success → `navigate('/products/' + created.product.id)` + success toast.

### 4.5 Product edit — `ProductEditPage` (`/products/:id/edit`)

Guarded like create. Loads the product first (`['product', id]`).

Form = `UpdateProductRequest` fields:
- Category `Select` (required, active categories + the product's current
  category even if it was since deactivated — include it so the value is
  representable)
- Name, Description
- Track inventory toggle
- **Active** toggle
- **Simple products only:** SKU, Barcode, Cost price, Selling price
- **Variant products:** those four fields are *not* rendered; a note reads
  *"Pricing and codes are managed per variant on the product page."*
- Submit → `buildUpdateProductRequest(product, { categoryId, name, description,
  trackInventory, isActive })` → `productsApi.update` →
  `navigate('/products/' + id)` + toast.

---

## 5. Cross-cutting behaviour

### Money / number formatting — `src/lib/format.ts`

- `formatMoney(n: number): string` — `₱` + `Intl.NumberFormat('en-PH', {
  minimumFractionDigits: 2 })`. **Currency is hard-coded to PHP for this phase
  and is not yet tenant-configurable** — documented here and in a code comment;
  a future settings slice may add a tenant currency, at which point this helper
  takes a currency argument. One helper, used everywhere.
- `formatRange(min, max, fmt)` — `fmt(min)` when equal, else `fmt(min) + ' – ' +
  fmt(max)`.
- `formatMarginPct(cost, selling)` — `null` when `cost == null` or
  `selling <= 0`; else `Math.round((selling - cost) / selling * 100) + '%'`.

### Safe product-update request builder — `src/lib/catalogRequests.ts`

```ts
// The ONE place an UpdateProductRequest is constructed. Used by ProductEditPage
// AND product reactivate/deactivate on ProductDetailPage.
buildUpdateProductRequest(
  product: ProductDto,
  patch: Partial<Pick<UpdateProductRequest,
    'categoryId' | 'name' | 'description' | 'trackInventory' | 'isActive'
    | 'sku' | 'barcode' | 'costPrice' | 'sellingPrice'>>,
): UpdateProductRequest
```

- Starts from the product's current `categoryId` / `name` / `description` /
  `trackInventory` / `isActive`, applies `patch`.
- **Simple product** (`!product.hasVariants`): `sku` / `barcode` / `costPrice`
  / `sellingPrice` come from `patch` (the edit form supplies them) or fall back
  to the product's current values.
- **Variant product** (`product.hasVariants`): forces `sku: null,
  barcode: null, costPrice: 0, sellingPrice: 0` and **ignores any of those
  keys in `patch`**. This is provably a no-op server-side (see §2 "Verified");
  the caller never passes them for a variant product anyway.
- A short comment in the file links to `ProductService.UpdateAsync` and states
  why the variant-product values are inert.

### Form error mapping — `src/lib/formErrors.ts`

```ts
fieldErrorsFrom(error: unknown): Record<string, string>   // '' when not a validation ApiError
// maps ApiError.fieldErrors (Record<string,string[]>) → first message per key, key lowercased
```
Plus a small `mapCodeToField(code)` for the conflict codes
(`CATEGORY_ALREADY_EXISTS` → `name`, `SKU_ALREADY_EXISTS` → `sku`,
`BARCODE_ALREADY_EXISTS` → `barcode`). Anything unmapped → toast the
`error.message`.

### Toasts

- Create → "Product created" / "Category created"
- Update → "Changes saved"
- Deactivate → "{name} deactivated" ; Reactivate → "{name} reactivated"
- Non-field API errors → `useToast()` error with `error.message`.

### Query invalidation

| Mutation | Invalidate |
| --- | --- |
| category create/update/deactivate | `['categories']` (prefix), `['categories','all-active']` |
| product create | `['products']` |
| product update/deactivate | `['products']`, `['product', id]` |
| variant create/update/deactivate | `['product', productId]`, `['products']` (price ranges change) |

### Not-found / 404

`ProductDetailPage` / `ProductEditPage`: an `ApiError` with status 404 →
`EmptyState` "Product not found" + link back to `/products` (do **not** trigger
the global 401 handler; 404 is not 401).

### Accessibility

- `Modal` / `ConfirmDialog`: focus trap (Tab **and** Shift+Tab wrap within the
  dialog), `Esc` closes, backdrop click closes, focus moves to the dialog on
  open and is **restored to the opener** on close, `aria-modal`, labelled by
  title, background scroll locked while open. Keep the implementation small but
  **manually verify every one of these** during the build (see §7) — do not
  assume a hand-rolled trap is correct.
- `Table` sort headers are `<button>`s inside `<th>` with `aria-sort`.
- All icon-only buttons have `aria-label`.
- Toggles are real checkboxes or `role="switch"` buttons with `aria-checked`.

---

## 6. File plan

```
web/negosio-web/src/
  api/
    catalog.ts                 NEW  categoriesApi / productsApi / variantsApi
    query-string.ts            NEW  qs() helper
    types.ts                   EDIT add catalog types, PagedResult, fix DashboardResponse
  hooks/
    usePagedQuery.ts           NEW
    useDebouncedValue.ts       NEW
  lib/
    useCan.ts                  NEW
    format.ts                  NEW  formatMoney / formatRange / formatMarginPct
    formErrors.ts              NEW  fieldErrorsFrom / mapCodeToField
    catalogRequests.ts         NEW  buildUpdateProductRequest (single UpdateProductRequest path)
    nav.ts                     EDIT grouped nav model; enable Products + Categories
  components/ui/
    Table.tsx                  NEW
    Pagination.tsx             NEW
    SearchInput.tsx            NEW
    Modal.tsx                  NEW
    ConfirmDialog.tsx          NEW
    index.ts                   EDIT barrel exports
  components/layout/
    Sidebar.tsx                EDIT render nav groups; product footer
  components/catalog/
    CategoryFormModal.tsx      NEW
    VariantFormModal.tsx       NEW
    ProductForm.tsx            NEW  shared create + edit — see note below
    VariantRowsField.tsx      NEW  the repeatable variant rows for the create form
    ProductFields.tsx         NEW  shared field groups (Details section), if ProductForm splits
  pages/
    CategoriesPage.tsx         NEW
    ProductsPage.tsx           NEW
    ProductDetailPage.tsx      NEW
    ProductCreatePage.tsx      NEW
    ProductEditPage.tsx        NEW
  App.tsx                      EDIT routes
```

No changes outside `web/negosio-web/`.

**`ProductForm` sharing rule:** start with one `ProductForm` taking a
`mode: 'create' | 'edit'` prop *only while the branching stays light* (a couple
of conditionals). The moment create-vs-edit divergence gets heavy — create has
the single-item/has-variants toggle + repeatable variant rows; edit has the
Active toggle and hides pricing for variant products — **split into
`ProductCreateForm` + `ProductEditForm`** and share the smaller pieces
(`ProductFields` for the Details section, `VariantRowsField`,
`useProductFormState`). Do not force one component to carry both shapes.

---

## 7. Testing / verification

There is **no frontend test runner** in this repo today (lint + `tsc` +
`vite build` only), and adding one is out of scope for this sub-project.
Verification is therefore:

1. `npm run lint` (oxlint) — clean.
2. `npm run build` (`tsc -b && vite build`) — clean, no TS errors.
3. **Manual browser walkthrough** against the real API (the smoke recipe used
   for Phase 2.5), driven headless + screenshotted:
   - Log in (Owner).
   - Categories: create "Beverages" + "Snacks"; edit; deactivate + reactivate;
     search; status filter; pagination (seed > 20 to see page 2).
   - Products: create a **simple** product (with stock tracking) → lands on
     detail. Create a **variant** product (2 variants) → detail shows the
     variants table. Edit each. Add a 3rd variant to the simple one → it
     converts. Deactivate / reactivate. Search by name and by SKU. Filter by
     category / status / tracking. Sort by name and price. Pagination.
   - Cost column: confirm it renders for Owner. (Role-switch check deferred —
     no staff-user creation UI yet; note it as a manual future check.)
   - 404: visit `/products/<bad-guid>` → not-found state.
   - Screenshots of: categories list, products list, product detail (simple),
     product detail (variants), create form (both modes).
4. **`Modal` behaviour — manual, explicit:** open a modal and verify Tab cycles
   only within it, **Shift+Tab** wraps backwards within it, `Esc` closes,
   backdrop click closes, focus lands in the dialog on open, focus returns to
   the triggering button on close, and the page behind does not scroll while
   open.
5. **`usePagedQuery` / browser history — manual, explicit:** set a search term +
   a filter + a sort + go to page 2; then (a) **refresh** → all of it is
   restored from the URL; (b) navigate into a product and press **Back** → the
   list returns with the same params; (c) confirm the URL query string reflects
   every param; (d) change the search / a filter / the sort → **page resets to
   1** each time.
6. Confirm no regression to Dashboard / Login / Register.

A follow-up sub-project may introduce Vitest + React Testing Library; if so,
these flows become the first specs to encode.

---

## 8. Open questions / risks

- **`all-active` categories via `pageSize: 100`** — kept as a stopgap behind
  the named constant `ACTIVE_CATEGORY_FETCH_LIMIT` (§4). Fine for Phase 3
  tenant sizes; if a tenant exceeds 100 categories the product form's category
  picker truncates. A typeahead/lookup endpoint is the real fix later.
- **Variant deactivation rules** — the backend owns the "can't remove the last
  active / default variant" logic; the UI surfaces whatever error it returns
  rather than duplicating the rule. Confirm the exact messages during the
  build and make them read well in a toast.
- **`UpdateProductRequest` price fields for variant products** — **RESOLVED,
  verified against the code** (see §2 "Verified"): the four fields are inside
  `if (!product.HasVariants)` in `ProductService.UpdateAsync` and cannot mutate
  a variant product; the validator accepts `0`/null. `buildUpdateProductRequest`
  is the single guarded construction site. Re-read that method if
  `ProductService` changes before the build; if the guard is ever removed, stop
  and report.
- **Currency** — `formatMoney` is hard-coded to PHP this phase; not tenant
  configurable yet (documented in `format.ts`).
- **App version in the footer** — needs a Vite `define` for
  `__APP_VERSION__` from `package.json`; if that's more friction than it's
  worth, just drop the footer line. Either way, no phase/roadmap text in the
  product UI.
