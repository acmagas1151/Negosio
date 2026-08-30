# Catalog Frontend Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Negosio web UI for product categories, products, and product variants, plus the shared frontend foundation (API modules, list/table/modal primitives, URL-synced list state, role-mirrored affordances) that the later Inventory / Registers / POS / Sales slices will reuse.

**Architecture:** Frontend-only. Add per-area API modules over the existing `apiRequest` client; add small focused UI primitives (`Table`, `Pagination`, `SearchInput`, `Modal`, `ConfirmDialog`) to `components/ui`; add `usePagedQuery` (list state ↔ URL) and `useCan` (role affordances). Five new full-page routes under the existing `ProtectedRoute` + `DashboardLayout`. TanStack Query for all server state; `useMutation` + query invalidation + toasts for writes. No backend change.

**Tech Stack:** React 19, TypeScript, Vite 8, React Router 7 (`Routes`/`Route`, `useSearchParams`), TanStack Query v5, Tailwind v4 (design tokens already defined), lucide-react, `oxlint`. No new npm dependencies.

**Spec:** `docs/superpowers/specs/2026-08-30-catalog-frontend-design.md` — read it before starting; the plan argues from it.

## Global Constraints

- **No new npm dependencies.** Everything is buildable from what `web/negosio-web/package.json` already has.
- **No backend / API-contract change.** If a real contract gap appears, stop and report — do not work around it.
- **No changes outside `web/negosio-web/`** (except this plan's own checkboxes and, at the end, the two memory files).
- **TypeScript:** the app's `tsconfig.app.json` sets `verbatimModuleSyntax: true` (every type-only import MUST be `import type { … }`), `erasableSyntaxOnly: true` (**no `enum`, no namespaces, no parameter properties** — use `const` objects + union types), `noUnusedLocals` / `noUnusedParameters`, `noFallthroughCasesInSwitch`. Target ES2023, DOM lib.
- **Lint:** `oxlint` with `react/rules-of-hooks: error` and `react/only-export-components: warn` — a file that exports a React component must not also export non-component values other than constants. Put hooks and helpers in `lib/` or `hooks/`, not alongside components.
- **Currency is hard-coded PHP this phase** (`formatMoney`), not tenant-configurable. Document in code.
- **Cost redaction is backend-authoritative.** `useCan('costs:view')` only decides whether a Cost column/field is rendered; **every** read of `minCostPrice` / `maxCostPrice` / `variant.costPrice` is independently null-checked. Never derive a cost from a selling price.
- **No phase/roadmap language in the product UI.** The sidebar footer becomes neutral branding or is dropped.
- **Per-task verification loop** (this repo has no frontend test runner, and adding one is out of scope — the spec's decision): after implementing, run `npx tsc -b` (clean) and `npm run lint` (clean); for page tasks also do the task's stated browser check; then commit. Pure-logic tasks additionally run a throwaway Node assertion snippet for a real red/green before implementing.
- **Working directory for all commands:** `web/negosio-web/`.
- **Commit style:** short imperative subject, no `Co-Authored-By` / "Generated with" trailer (repo convention). Commit at the end of each task.
- **Money / peso:** the `₱` glyph is used directly in source (UTF-8 files).

---

## File Structure

```
web/negosio-web/src/
  api/
    query-string.ts        NEW  qs(params) -> "?a=1&b=2"
    catalog.ts             NEW  categoriesApi, productsApi, variantsApi, ACTIVE_CATEGORY_FETCH_LIMIT
    types.ts               EDIT add PagedResult<T> + all catalog types; fix DashboardResponse
  hooks/
    useDebouncedValue.ts   NEW  useDebouncedValue<T>(value, ms)
    usePagedQuery.ts       NEW  list state <-> URL (page/q/sort/dir/filters)
  lib/
    useCan.ts              NEW  useCan('catalog:write' | 'costs:view')
    format.ts              NEW  formatMoney / formatRange / formatMarginPct
    formErrors.ts          NEW  fieldErrorsFrom / mapCodeToField
    catalogRequests.ts     NEW  buildUpdateProductRequest(product, patch)
    nav.ts                 EDIT grouped nav model; enable Products + Categories
  components/ui/
    Modal.tsx              NEW
    ConfirmDialog.tsx      NEW
    Table.tsx              NEW  Table + Table.HeaderCell (sortable) + Table.Row/Cell
    Pagination.tsx         NEW
    SearchInput.tsx        NEW
    index.ts              EDIT barrel exports
  components/layout/
    Sidebar.tsx           EDIT render nav groups; neutral footer
  components/catalog/
    CategoryFormModal.tsx  NEW
    VariantFormModal.tsx   NEW
    ProductFields.tsx      NEW  shared "Details" field group (category/name/description/trackInventory)
    VariantRowsField.tsx   NEW  repeatable variant rows (create form)
    ProductCreateForm.tsx  NEW
    ProductEditForm.tsx    NEW
  pages/
    CategoriesPage.tsx     NEW
    ProductsPage.tsx       NEW
    ProductDetailPage.tsx  NEW
    ProductCreatePage.tsx  NEW
    ProductEditPage.tsx    NEW
  App.tsx                 EDIT routes
```

The spec floated a single `ProductForm` with a `mode` prop; this plan goes straight to `ProductCreateForm` + `ProductEditForm` sharing `ProductFields`, because the two diverge substantially (create has the single-item/variants toggle + repeatable rows; edit has the Active toggle and hides pricing for variant products). That is the spec's own fallback and avoids one component carrying both shapes.

---

## Task 1: API types + `PagedResult` + `qs()` helper

**Files:**
- Create: `src/api/query-string.ts`
- Modify: `src/api/types.ts` (append catalog types; replace `DashboardResponse`)
- Throwaway: `/tmp/t1.mjs` (deleted in the task)

**Interfaces:**
- Produces:
  - `qs(params: Record<string, string | number | boolean | undefined | null>): string` — `""` when nothing to serialise, else `"?k=v&k2=v2"`, URL-encoded, dropping `undefined` / `null` / `""`.
  - Types (all `export interface` / `export type` in `src/api/types.ts`):
    ```ts
    export interface PagedResult<T> { items: T[]; page: number; pageSize: number; totalCount: number; totalPages: number }

    export interface CategoryDto { id: string; name: string; description: string | null; isActive: boolean; productCount: number; createdAtUtc: string; updatedAtUtc: string }
    export interface CreateCategoryRequest { name: string; description: string | null }
    export interface UpdateCategoryRequest { name: string; description: string | null; isActive: boolean }
    export interface CategoryListParams { search?: string; isActive?: boolean; page?: number; pageSize?: number }

    export interface ProductVariantDto { id: string; name: string; isDefault: boolean; sku: string | null; barcode: string | null; costPrice: number | null; sellingPrice: number; isActive: boolean; createdAtUtc: string; updatedAtUtc: string }
    export interface ProductDto {
      id: string; categoryId: string; categoryName: string; name: string; description: string | null;
      trackInventory: boolean; isActive: boolean; hasVariants: boolean; variantCount: number;
      sku: string | null; barcode: string | null;
      minCostPrice: number | null; maxCostPrice: number | null;
      minSellingPrice: number; maxSellingPrice: number;
      createdAtUtc: string; updatedAtUtc: string
    }
    export interface ProductDetailDto { product: ProductDto; variants: ProductVariantDto[] }
    export interface VariantInput { name: string; sku: string | null; barcode: string | null; costPrice: number; sellingPrice: number }
    export interface CreateProductRequest {
      categoryId: string; name: string; description: string | null; trackInventory: boolean;
      sku: string | null; barcode: string | null; costPrice: number; sellingPrice: number;
      variants: VariantInput[] | null
    }
    export interface UpdateProductRequest {
      categoryId: string; name: string; description: string | null; trackInventory: boolean; isActive: boolean;
      sku: string | null; barcode: string | null; costPrice: number; sellingPrice: number
    }
    export type ProductSortBy = 'name' | 'sellingprice' | 'createdatutc'
    export interface ProductListParams {
      search?: string; categoryId?: string; isActive?: boolean; trackInventory?: boolean;
      page?: number; pageSize?: number; sortBy?: ProductSortBy; sortDirection?: 'asc' | 'desc'
    }
    ```
  - Replacement `DashboardResponse` (Dashboard page still renders only its current cards; this is type hygiene):
    ```ts
    export interface DashboardResponse {
      tenant: { id: string; name: string; businessType: BusinessType }
      branchCount: number
      userCount: number
      totalProducts: number
      activeCategories: number
      lowStockItems: number
      todaysSales: number
      todaysTransactions: number
      averageTransactionValue: number
    }
    ```

- [ ] **Step 1: Write the throwaway failing check**

Create `/tmp/t1.mjs`:

```js
import { qs } from '../c/Users/Ace/Documents/Negosio/web/negosio-web/src/api/query-string.ts'
```

That import path won't work from Node directly; instead inline-test the logic. Replace the file with:

```js
// Mirror of the intended qs() behaviour — asserts the spec before implementing.
const qs = (await import('file:///c:/Users/Ace/Documents/Negosio/web/negosio-web/src/api/query-string.ts')).qs
import assert from 'node:assert'
assert.equal(qs({}), '')
assert.equal(qs({ a: 1, b: 'x' }), '?a=1&b=x')
assert.equal(qs({ a: undefined, b: null, c: '', d: 0, e: false }), '?d=0&e=false')
assert.equal(qs({ q: 'a b&c' }), '?q=a%20b%26c')
console.log('ok')
```

- [ ] **Step 2: Run it, verify it fails**

Run: `node /tmp/t1.mjs`
Expected: FAIL — module not found (`query-string.ts` does not exist yet).

- [ ] **Step 3: Implement `qs()`**

Create `src/api/query-string.ts`:

```ts
/** Serialise a flat params object to a query string, dropping undefined / null / empty-string. */
export function qs(
  params: Record<string, string | number | boolean | undefined | null>,
): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value === undefined || value === null || value === '') continue
    search.set(key, String(value))
  }
  const s = search.toString()
  return s ? `?${s}` : ''
}
```

- [ ] **Step 4: Add the types**

Append every type from the Interfaces block above to `src/api/types.ts`. Replace the existing `DashboardResponse` interface with the version above. Keep `import type { BusinessType }` usage valid — `BusinessType` is already declared in that file.

- [ ] **Step 5: Run the check, verify it passes**

Run: `node /tmp/t1.mjs`
Expected: `ok`
(If Node can't resolve the `.ts` import, run `npx tsx /tmp/t1.mjs` instead — `tsx` is not a dependency, so alternatively skip and rely on Step 6 + a manual read-through of the 6-line function.)

- [ ] **Step 6: Typecheck + lint + clean up**

Run: `npx tsc -b`
Expected: no errors.
Run: `npm run lint`
Expected: no errors.
Run: `rm /tmp/t1.mjs`

- [ ] **Step 7: Commit**

```bash
git add src/api/query-string.ts src/api/types.ts
git commit -m "web: catalog API types, PagedResult, qs() helper"
```

---

## Task 2: Catalog API modules

**Files:**
- Create: `src/api/catalog.ts`

**Interfaces:**
- Consumes: `apiRequest` from `./client`; all types from `./types`; `qs` from `./query-string`.
- Produces:
  ```ts
  export const ACTIVE_CATEGORY_FETCH_LIMIT = 100 // stopgap: no typeahead endpoint yet

  export const categoriesApi: {
    list(params: CategoryListParams): Promise<PagedResult<CategoryDto>>
    listAllActive(): Promise<CategoryDto[]>          // items of list({ isActive: true, pageSize: ACTIVE_CATEGORY_FETCH_LIMIT })
    get(id: string): Promise<CategoryDto>
    create(body: CreateCategoryRequest): Promise<CategoryDto>
    update(id: string, body: UpdateCategoryRequest): Promise<CategoryDto>
    deactivate(id: string): Promise<void>
  }
  export const productsApi: {
    list(params: ProductListParams): Promise<PagedResult<ProductDto>>
    get(id: string): Promise<ProductDetailDto>
    create(body: CreateProductRequest): Promise<ProductDetailDto>
    update(id: string, body: UpdateProductRequest): Promise<ProductDetailDto>
    deactivate(id: string): Promise<void>
  }
  export const variantsApi: {
    list(productId: string): Promise<ProductVariantDto[]>
    create(productId: string, body: VariantInput): Promise<ProductVariantDto>
    update(productId: string, variantId: string, body: VariantInput): Promise<ProductVariantDto>
    deactivate(productId: string, variantId: string): Promise<void>
  }
  ```

- [ ] **Step 1: Implement `src/api/catalog.ts`**

```ts
import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  CategoryDto,
  CategoryListParams,
  CreateCategoryRequest,
  UpdateCategoryRequest,
  CreateProductRequest,
  UpdateProductRequest,
  PagedResult,
  ProductDetailDto,
  ProductDto,
  ProductListParams,
  ProductVariantDto,
  VariantInput,
} from './types'

/** Stopgap page size for the "all active categories" picker feed — no typeahead endpoint exists yet. */
export const ACTIVE_CATEGORY_FETCH_LIMIT = 100

export const categoriesApi = {
  list: (params: CategoryListParams) =>
    apiRequest<PagedResult<CategoryDto>>(`/api/categories${qs({ ...params })}`),

  listAllActive: async (): Promise<CategoryDto[]> => {
    const page = await apiRequest<PagedResult<CategoryDto>>(
      `/api/categories${qs({ isActive: true, pageSize: ACTIVE_CATEGORY_FETCH_LIMIT })}`,
    )
    return page.items
  },

  get: (id: string) => apiRequest<CategoryDto>(`/api/categories/${id}`),

  create: (body: CreateCategoryRequest) =>
    apiRequest<CategoryDto>('/api/categories', { method: 'POST', body }),

  update: (id: string, body: UpdateCategoryRequest) =>
    apiRequest<CategoryDto>(`/api/categories/${id}`, { method: 'PUT', body }),

  deactivate: (id: string) =>
    apiRequest<void>(`/api/categories/${id}`, { method: 'DELETE' }),
}

export const productsApi = {
  list: (params: ProductListParams) =>
    apiRequest<PagedResult<ProductDto>>(`/api/products${qs({ ...params })}`),

  get: (id: string) => apiRequest<ProductDetailDto>(`/api/products/${id}`),

  create: (body: CreateProductRequest) =>
    apiRequest<ProductDetailDto>('/api/products', { method: 'POST', body }),

  update: (id: string, body: UpdateProductRequest) =>
    apiRequest<ProductDetailDto>(`/api/products/${id}`, { method: 'PUT', body }),

  deactivate: (id: string) =>
    apiRequest<void>(`/api/products/${id}`, { method: 'DELETE' }),
}

export const variantsApi = {
  list: (productId: string) =>
    apiRequest<ProductVariantDto[]>(`/api/products/${productId}/variants`),

  create: (productId: string, body: VariantInput) =>
    apiRequest<ProductVariantDto>(`/api/products/${productId}/variants`, { method: 'POST', body }),

  update: (productId: string, variantId: string, body: VariantInput) =>
    apiRequest<ProductVariantDto>(`/api/products/${productId}/variants/${variantId}`, {
      method: 'PUT',
      body,
    }),

  deactivate: (productId: string, variantId: string) =>
    apiRequest<void>(`/api/products/${productId}/variants/${variantId}`, { method: 'DELETE' }),
}
```

Note: `apiRequest` already returns `undefined as T` for `204`, so `Promise<void>` is honest.

- [ ] **Step 2: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add src/api/catalog.ts
git commit -m "web: catalog API modules (categories, products, variants)"
```

---

## Task 3: Formatting + form-error + update-request helpers

**Files:**
- Create: `src/lib/format.ts`, `src/lib/formErrors.ts`, `src/lib/catalogRequests.ts`
- Throwaway: `/tmp/t3.mjs`

**Interfaces:**
- Consumes: `ApiError` from `../api/client`; `ProductDto`, `UpdateProductRequest` from `../api/types`.
- Produces:
  ```ts
  // format.ts
  export function formatMoney(n: number): string          // "₱1,250.00"
  export function formatRange(min: number, max: number, fmt: (n: number) => string): string
  export function formatMarginPct(cost: number | null, selling: number): string | null

  // formErrors.ts
  export function fieldErrorsFrom(error: unknown): Record<string, string>  // lowercased keys, first msg; {} if not a 400+errors ApiError
  export function mapCodeToField(code: string): string | null             // CATEGORY_ALREADY_EXISTS->'name', SKU_ALREADY_EXISTS->'sku', BARCODE_ALREADY_EXISTS->'barcode'

  // catalogRequests.ts
  export function buildUpdateProductRequest(
    product: ProductDto,
    patch: Partial<Pick<UpdateProductRequest,
      'categoryId'|'name'|'description'|'trackInventory'|'isActive'|'sku'|'barcode'|'costPrice'|'sellingPrice'>>,
  ): UpdateProductRequest
  ```

- [ ] **Step 1: Write the throwaway failing check**

Create `/tmp/t3.mjs`:

```js
import assert from 'node:assert'
const { formatMoney, formatRange, formatMarginPct } =
  await import('file:///c:/Users/Ace/Documents/Negosio/web/negosio-web/src/lib/format.ts')
const { buildUpdateProductRequest } =
  await import('file:///c:/Users/Ace/Documents/Negosio/web/negosio-web/src/lib/catalogRequests.ts')

assert.equal(formatMoney(1250), '₱1,250.00')
assert.equal(formatMoney(0), '₱0.00')
assert.equal(formatRange(5, 5, formatMoney), '₱5.00')
assert.equal(formatRange(5, 9, formatMoney), '₱5.00 – ₱9.00')
assert.equal(formatMarginPct(null, 10), null)
assert.equal(formatMarginPct(4, 0), null)
assert.equal(formatMarginPct(4, 10), '60%')

const simple = { id: 'p1', hasVariants: false, categoryId: 'c1', name: 'Coke', description: null,
  trackInventory: true, isActive: true, sku: 'S1', barcode: null, minCostPrice: 4, minSellingPrice: 10 }
const r1 = buildUpdateProductRequest(simple, { name: 'Coke Zero' })
assert.equal(r1.name, 'Coke Zero'); assert.equal(r1.sku, 'S1'); assert.equal(r1.sellingPrice, 10)

const variant = { ...simple, id: 'p2', hasVariants: true, sku: null, minCostPrice: null }
const r2 = buildUpdateProductRequest(variant, { isActive: false, sku: 'HACK', costPrice: 999 })
assert.equal(r2.isActive, false); assert.equal(r2.sku, null); assert.equal(r2.costPrice, 0); assert.equal(r2.sellingPrice, 0)
console.log('ok')
```

- [ ] **Step 2: Run it, verify it fails**

Run: `node /tmp/t3.mjs`
Expected: FAIL — modules not found.
(If Node cannot import `.ts`, note it and verify by reading after Step 3–5; `npx tsc -b` in Step 6 still gates types.)

- [ ] **Step 3: Implement `src/lib/format.ts`**

```ts
// Currency is hard-coded to PHP for this phase — Negosio is not yet multi-currency
// and tenants cannot configure it. A later settings slice adds a currency argument here.
const pesoFormatter = new Intl.NumberFormat('en-PH', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
})

export function formatMoney(n: number): string {
  return `₱${pesoFormatter.format(n)}`
}

export function formatRange(min: number, max: number, fmt: (n: number) => string): string {
  return min === max ? fmt(min) : `${fmt(min)} – ${fmt(max)}`
}

/** Gross margin as a whole-number percent, or null when cost is unknown / selling is non-positive. */
export function formatMarginPct(cost: number | null, selling: number): string | null {
  if (cost === null || selling <= 0) return null
  return `${Math.round(((selling - cost) / selling) * 100)}%`
}
```

- [ ] **Step 4: Implement `src/lib/formErrors.ts`**

```ts
import { ApiError } from '../api/client'

/**
 * Flatten an ApiError's field errors to `{ fieldnamelower: firstMessage }`.
 * Returns `{}` for anything that isn't a validation ApiError.
 */
export function fieldErrorsFrom(error: unknown): Record<string, string> {
  if (!(error instanceof ApiError) || !error.fieldErrors) return {}
  const out: Record<string, string> = {}
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    if (messages.length > 0) out[key.toLowerCase()] = messages[0]
  }
  return out
}

const CODE_TO_FIELD: Record<string, string> = {
  CATEGORY_ALREADY_EXISTS: 'name',
  SKU_ALREADY_EXISTS: 'sku',
  BARCODE_ALREADY_EXISTS: 'barcode',
}

/** Which form field a conflict `code` should attach to, or null to fall back to a toast. */
export function mapCodeToField(code: string): string | null {
  return CODE_TO_FIELD[code] ?? null
}
```

- [ ] **Step 5: Implement `src/lib/catalogRequests.ts`**

```ts
import type { ProductDto, UpdateProductRequest } from '../api/types'

type ProductPatch = Partial<
  Pick<
    UpdateProductRequest,
    | 'categoryId'
    | 'name'
    | 'description'
    | 'trackInventory'
    | 'isActive'
    | 'sku'
    | 'barcode'
    | 'costPrice'
    | 'sellingPrice'
  >
>

/**
 * The ONE place an UpdateProductRequest is built — used by ProductEditForm AND by
 * deactivate / reactivate on ProductDetailPage.
 *
 * For a VARIANT product (`product.hasVariants`), sku/barcode/costPrice/sellingPrice are forced to
 * inert values and any such keys in `patch` are ignored. Verified in
 * src/Negosio.Application/Catalog/ProductService.cs (UpdateAsync): those four fields are only read
 * inside `if (!product.HasVariants)`, and UpdateProductRequestValidator accepts costPrice/sellingPrice = 0.
 */
export function buildUpdateProductRequest(product: ProductDto, patch: ProductPatch): UpdateProductRequest {
  const base = {
    categoryId: patch.categoryId ?? product.categoryId,
    name: patch.name ?? product.name,
    description: patch.description !== undefined ? patch.description : product.description,
    trackInventory: patch.trackInventory ?? product.trackInventory,
    isActive: patch.isActive ?? product.isActive,
  }

  if (product.hasVariants) {
    return { ...base, sku: null, barcode: null, costPrice: 0, sellingPrice: 0 }
  }

  return {
    ...base,
    sku: patch.sku !== undefined ? patch.sku : product.sku,
    barcode: patch.barcode !== undefined ? patch.barcode : product.barcode,
    costPrice: patch.costPrice ?? product.minCostPrice ?? 0,
    sellingPrice: patch.sellingPrice ?? product.minSellingPrice,
  }
}
```

- [ ] **Step 6: Run the check + typecheck + lint + clean up**

Run: `node /tmp/t3.mjs` — expected: `ok` (or skip per Step 2 note).
Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.
Run: `rm /tmp/t3.mjs`

- [ ] **Step 7: Commit**

```bash
git add src/lib/format.ts src/lib/formErrors.ts src/lib/catalogRequests.ts
git commit -m "web: money/margin formatting, form-error mapping, safe product-update builder"
```

---

## Task 4: `useCan` capability hook

**Files:**
- Create: `src/lib/useCan.ts`

**Interfaces:**
- Consumes: `useAuth` from `../auth/AuthContext`; `UserRole` from `../api/types`.
- Produces: `export function useCan(capability: 'catalog:write' | 'costs:view'): boolean`

- [ ] **Step 1: Implement `src/lib/useCan.ts`**

```ts
import { useAuth } from '../auth/AuthContext'
import type { UserRole } from '../api/types'

type Capability = 'catalog:write' | 'costs:view'

// Mirrors src/Negosio.Application/Catalog/CatalogAccess.cs — keep in sync if the backend sets change.
const CAPABILITY_ROLES: Record<Capability, ReadonlySet<UserRole>> = {
  'catalog:write': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'costs:view': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'InventoryStaff']),
}

/**
 * UX affordance only — the backend independently enforces every rule and redacts cost fields.
 * Never gate a cost *value* on this; null-check the API field instead.
 */
export function useCan(capability: Capability): boolean {
  const { user } = useAuth()
  if (!user) return false
  return CAPABILITY_ROLES[capability].has(user.role)
}
```

- [ ] **Step 2: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors (`useCan` is a hook in `lib/`, no component export clash).

- [ ] **Step 3: Commit**

```bash
git add src/lib/useCan.ts
git commit -m "web: useCan capability hook (mirrors CatalogAccess role sets)"
```

---

## Task 5: `useDebouncedValue` + `usePagedQuery`

**Files:**
- Create: `src/hooks/useDebouncedValue.ts`, `src/hooks/usePagedQuery.ts`

**Interfaces:**
- Consumes: `useSearchParams` from `react-router-dom`.
- Produces:
  ```ts
  export function useDebouncedValue<T>(value: T, ms: number): T

  export interface PagedQueryState<F extends Record<string, string | undefined>> {
    page: number
    pageSize: number
    search: string          // debounced — use for query keys / requests
    searchInput: string     // raw — bind to <SearchInput value>
    setSearchInput: (v: string) => void
    filters: F
    setFilter: (key: keyof F, value: string | undefined) => void
    sortBy: string | undefined
    sortDirection: 'asc' | 'desc' | undefined
    setSort: (column: string) => void   // asc -> desc -> cleared, cycling
    setPage: (page: number) => void
  }

  export function usePagedQuery<F extends Record<string, string | undefined>>(opts: {
    defaultFilters: F
    defaultPageSize?: number   // 20
    debounceMs?: number        // 350
  }): PagedQueryState<F>
  ```

**Behaviour requirements (from spec §3.3):**
- State lives in the URL via `useSearchParams`. Reserved keys: `page`, `q`, `sort`, `dir`; plus one key per filter (the filter object's own keys).
- `searchInput` is local state seeded from `q`; `search` is `useDebouncedValue(searchInput, debounceMs)`. When the **debounced** value changes, write `q` (or delete it if empty) **and reset `page` to 1**.
- `setFilter` writes/removes that filter's key **and resets `page` to 1**.
- `setSort(column)`: if not currently sorted by `column` → `sort=column&dir=asc`; if `asc` → `dir=desc`; if `desc` → remove `sort` & `dir` (back to default). Any of these **resets `page` to 1**.
- `setPage(n)` writes `page` (removes the key when `n === 1`).
- `page` parses from `q` param `page` (default 1, min 1). `pageSize` is fixed = `defaultPageSize`.
- Reading `filters`: for each key in `defaultFilters`, value = `searchParams.get(key) ?? undefined`.

- [ ] **Step 1: Implement `src/hooks/useDebouncedValue.ts`**

```ts
import { useEffect, useState } from 'react'

/** Returns `value` after it has stopped changing for `ms` milliseconds. */
export function useDebouncedValue<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value)

  useEffect(() => {
    const id = setTimeout(() => setDebounced(value), ms)
    return () => clearTimeout(id)
  }, [value, ms])

  return debounced
}
```

- [ ] **Step 2: Implement `src/hooks/usePagedQuery.ts`**

```ts
import { useCallback, useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useDebouncedValue } from './useDebouncedValue'

export interface PagedQueryState<F extends Record<string, string | undefined>> {
  page: number
  pageSize: number
  search: string
  searchInput: string
  setSearchInput: (v: string) => void
  filters: F
  setFilter: (key: keyof F, value: string | undefined) => void
  sortBy: string | undefined
  sortDirection: 'asc' | 'desc' | undefined
  setSort: (column: string) => void
  setPage: (page: number) => void
}

const RESERVED = new Set(['page', 'q', 'sort', 'dir'])

export function usePagedQuery<F extends Record<string, string | undefined>>(opts: {
  defaultFilters: F
  defaultPageSize?: number
  debounceMs?: number
}): PagedQueryState<F> {
  const { defaultFilters, defaultPageSize = 20, debounceMs = 350 } = opts
  const [params, setParams] = useSearchParams()

  const pageSize = defaultPageSize
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1)
  const sortBy = params.get('sort') ?? undefined
  const sortDirection = (params.get('dir') as 'asc' | 'desc' | null) ?? undefined

  const filterKeys = useMemo(() => Object.keys(defaultFilters), [defaultFilters])
  const filters = useMemo(() => {
    const out = {} as F
    for (const key of filterKeys) {
      ;(out as Record<string, string | undefined>)[key] = params.get(key) ?? undefined
    }
    return out
  }, [params, filterKeys])

  const [searchInput, setSearchInput] = useState(params.get('q') ?? '')
  const search = useDebouncedValue(searchInput, debounceMs)

  // Mutate the URL immutably; always drop `page` on any filter/search/sort change.
  const patch = useCallback(
    (mutate: (next: URLSearchParams) => void) => {
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          mutate(next)
          return next
        },
        { replace: false },
      )
    },
    [setParams],
  )

  // Push the debounced search term into the URL (and reset page) when it settles.
  useEffect(() => {
    const current = params.get('q') ?? ''
    if (current === search) return
    patch((next) => {
      if (search) next.set('q', search)
      else next.delete('q')
      next.delete('page')
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search])

  const setFilter = useCallback(
    (key: keyof F, value: string | undefined) => {
      patch((next) => {
        if (value) next.set(String(key), value)
        else next.delete(String(key))
        next.delete('page')
      })
    },
    [patch],
  )

  const setSort = useCallback(
    (column: string) => {
      patch((next) => {
        const by = next.get('sort')
        const dir = next.get('dir')
        if (by !== column) {
          next.set('sort', column)
          next.set('dir', 'asc')
        } else if (dir === 'asc') {
          next.set('dir', 'desc')
        } else {
          next.delete('sort')
          next.delete('dir')
        }
        next.delete('page')
      })
    },
    [patch],
  )

  const setPage = useCallback(
    (n: number) => {
      patch((next) => {
        if (n <= 1) next.delete('page')
        else next.set('page', String(n))
      })
    },
    [patch],
  )

  // Guard against a stale RESERVED-key check tripping lint on the unused import path.
  void RESERVED

  return {
    page,
    pageSize,
    search,
    searchInput,
    setSearchInput,
    filters,
    setFilter,
    sortBy,
    sortDirection,
    setSort,
    setPage,
  }
}
```

Note on the `RESERVED`/`void` line: if `oxlint` or `tsc` flags `RESERVED` as unused, delete both the `const RESERVED` and the `void RESERVED` line — it is only documentation. Keep whichever leaves lint clean.

- [ ] **Step 3: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors. If `react-hooks/exhaustive-deps` is not a configured rule, remove the disable comment.

- [ ] **Step 4: Commit**

```bash
git add src/hooks/useDebouncedValue.ts src/hooks/usePagedQuery.ts
git commit -m "web: useDebouncedValue + usePagedQuery (list state synced to URL)"
```

---

## Task 6: `Modal` + `ConfirmDialog` primitives

**Files:**
- Create: `src/components/ui/Modal.tsx`, `src/components/ui/ConfirmDialog.tsx`
- Modify: `src/components/ui/index.ts`

**Interfaces:**
- Consumes: `createPortal` from `react-dom`; `Button` from `./Button`; `cn` from `../../lib/cn`; `X` from `lucide-react`.
- Produces:
  ```ts
  export function Modal(props: {
    open: boolean
    onClose: () => void
    title: string
    children: React.ReactNode
    footer?: React.ReactNode
    /** default 'md' */
    size?: 'sm' | 'md'
  }): React.ReactNode

  export function ConfirmDialog(props: {
    open: boolean
    onClose: () => void
    onConfirm: () => void
    title: string
    message: React.ReactNode
    confirmLabel?: string      // default 'Confirm'
    tone?: 'default' | 'danger' // default 'default'
    loading?: boolean
  }): React.ReactNode
  ```

**Behaviour requirements (spec §3.5 / §5 accessibility):** portal to `document.body`; render nothing when `!open`; backdrop click closes; `Escape` closes; focus moves into the dialog on open; focus returns to the previously-focused element on close; `Tab` and `Shift+Tab` stay within the dialog (wrap at both ends); `role="dialog"`, `aria-modal="true"`, `aria-labelledby` the title; lock `document.body` scroll while open; close button (`X`, `aria-label="Close"`) top-right.

- [ ] **Step 1: Implement `src/components/ui/Modal.tsx`**

```tsx
import { useCallback, useEffect, useId, useRef } from 'react'
import { createPortal } from 'react-dom'
import { X } from 'lucide-react'
import { cn } from '../../lib/cn'

interface ModalProps {
  open: boolean
  onClose: () => void
  title: string
  children: React.ReactNode
  footer?: React.ReactNode
  size?: 'sm' | 'md'
}

const FOCUSABLE =
  'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])'

export function Modal({ open, onClose, title, children, footer, size = 'md' }: ModalProps) {
  const panelRef = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const restoreFocusRef = useRef<HTMLElement | null>(null)

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        onClose()
        return
      }
      if (e.key !== 'Tab' || !panelRef.current) return
      const items = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE))
      if (items.length === 0) return
      const first = items[0]
      const last = items[items.length - 1]
      const active = document.activeElement
      if (e.shiftKey && active === first) {
        e.preventDefault()
        last.focus()
      } else if (!e.shiftKey && active === last) {
        e.preventDefault()
        first.focus()
      }
    },
    [onClose],
  )

  useEffect(() => {
    if (!open) return
    restoreFocusRef.current = document.activeElement as HTMLElement | null
    const { overflow } = document.body.style
    document.body.style.overflow = 'hidden'
    // Focus the first focusable node in the panel (fall back to the panel itself).
    const raf = requestAnimationFrame(() => {
      const target =
        panelRef.current?.querySelector<HTMLElement>(FOCUSABLE) ?? panelRef.current
      target?.focus()
    })
    return () => {
      cancelAnimationFrame(raf)
      document.body.style.overflow = overflow
      restoreFocusRef.current?.focus?.()
    }
  }, [open])

  if (!open) return null

  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4"
      onKeyDown={handleKeyDown}
    >
      <button
        type="button"
        aria-label="Close"
        tabIndex={-1}
        onClick={onClose}
        className="absolute inset-0 bg-text-primary/40"
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className={cn(
          'relative z-10 w-full rounded-2xl bg-surface p-5 shadow-card-lg outline-none',
          size === 'sm' ? 'max-w-sm' : 'max-w-lg',
        )}
      >
        <div className="mb-4 flex items-start justify-between gap-4">
          <h2 id={titleId} className="text-lg font-bold text-text-primary">
            {title}
          </h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            className="-m-1.5 rounded-lg p-1.5 text-text-secondary hover:bg-surface-subtle"
          >
            <X className="size-5" aria-hidden="true" />
          </button>
        </div>
        <div className="text-sm text-text-secondary">{children}</div>
        {footer && <div className="mt-6 flex justify-end gap-2">{footer}</div>}
      </div>
    </div>,
    document.body,
  )
}
```

If the `shadow-card-lg` token is not defined in `index.css`, use `shadow-xl`. Verify by grepping `web/negosio-web/src/index.css` for `shadow-card-lg` (it is used by `DashboardLayout`, so it exists).

- [ ] **Step 2: Implement `src/components/ui/ConfirmDialog.tsx`**

```tsx
import { Button } from './Button'
import { Modal } from './Modal'

interface ConfirmDialogProps {
  open: boolean
  onClose: () => void
  onConfirm: () => void
  title: string
  message: React.ReactNode
  confirmLabel?: string
  tone?: 'default' | 'danger'
  loading?: boolean
}

export function ConfirmDialog({
  open,
  onClose,
  onConfirm,
  title,
  message,
  confirmLabel = 'Confirm',
  tone = 'default',
  loading = false,
}: ConfirmDialogProps) {
  return (
    <Modal
      open={open}
      onClose={onClose}
      title={title}
      size="sm"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={loading}>
            Cancel
          </Button>
          <Button
            variant={tone === 'danger' ? 'destructive' : 'primary'}
            size="sm"
            onClick={onConfirm}
            loading={loading}
          >
            {confirmLabel}
          </Button>
        </>
      }
    >
      {message}
    </Modal>
  )
}
```

- [ ] **Step 3: Export from the barrel**

In `src/components/ui/index.ts` add:

```ts
export { Modal } from './Modal'
export { ConfirmDialog } from './ConfirmDialog'
```

- [ ] **Step 4: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 5: Manual browser check (temporary harness)**

Temporarily add to `DashboardPage.tsx` a button that toggles a `Modal` with two text inputs and a footer button. Run `npm run dev`, open `/dashboard`, and verify **every** item: Tab cycles within the modal, Shift+Tab wraps backward, `Esc` closes, backdrop click closes, focus lands inside on open, focus returns to the trigger button on close, background does not scroll while open. Then **revert the harness** (`git checkout src/pages/DashboardPage.tsx`).

- [ ] **Step 6: Commit**

```bash
git add src/components/ui/Modal.tsx src/components/ui/ConfirmDialog.tsx src/components/ui/index.ts
git commit -m "web: Modal + ConfirmDialog primitives (focus trap, esc, scroll lock)"
```

---

## Task 7: `Table` + `Pagination` + `SearchInput` primitives

**Files:**
- Create: `src/components/ui/Table.tsx`, `src/components/ui/Pagination.tsx`, `src/components/ui/SearchInput.tsx`
- Modify: `src/components/ui/index.ts`

**Interfaces:**
- Produces:
  ```ts
  export function Table(props: { children: React.ReactNode; className?: string }): React.ReactNode
  export namespace Table {}  // not literally — the following are attached as Table.X
  // Table.Head    : { children }           -> <thead><tr>
  // Table.Body    : { children }           -> <tbody>
  // Table.Row     : { children; className? } -> <tr>
  // Table.HeaderCell : { children; align?: 'left'|'right'; sortKey?: string; activeSort?: {by?: string; dir?: 'asc'|'desc'}; onSort?: (key: string) => void }
  // Table.Cell    : { children; align?: 'left'|'right'; className? } -> <td>

  export function Pagination(props: {
    page: number
    pageSize: number
    totalCount: number
    totalPages: number
    onPageChange: (page: number) => void
  }): React.ReactNode      // renders null when totalPages <= 1

  export function SearchInput(props: {
    value: string
    onChange: (value: string) => void
    placeholder?: string
    className?: string
  }): React.ReactNode
  ```

- [ ] **Step 1: Implement `src/components/ui/Table.tsx`**

```tsx
import { ArrowDown, ArrowUp, ChevronsUpDown } from 'lucide-react'
import { cn } from '../../lib/cn'

function Table({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <div className="overflow-x-auto rounded-xl border border-border bg-surface">
      <table className={cn('w-full text-sm', className)}>{children}</table>
    </div>
  )
}

function Head({ children }: { children: React.ReactNode }) {
  return (
    <thead className="border-b border-border bg-surface-subtle">
      <tr>{children}</tr>
    </thead>
  )
}

function Body({ children }: { children: React.ReactNode }) {
  return <tbody className="divide-y divide-border">{children}</tbody>
}

function Row({ children, className }: { children: React.ReactNode; className?: string }) {
  return <tr className={cn('hover:bg-surface-subtle/60', className)}>{children}</tr>
}

interface HeaderCellProps {
  children: React.ReactNode
  align?: 'left' | 'right'
  sortKey?: string
  activeSort?: { by?: string; dir?: 'asc' | 'desc' }
  onSort?: (key: string) => void
}

function HeaderCell({ children, align = 'left', sortKey, activeSort, onSort }: HeaderCellProps) {
  const sortable = !!sortKey && !!onSort
  const isActive = sortable && activeSort?.by === sortKey
  const ariaSort = isActive ? (activeSort?.dir === 'desc' ? 'descending' : 'ascending') : 'none'

  return (
    <th
      scope="col"
      aria-sort={sortable ? ariaSort : undefined}
      className={cn(
        'px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-text-muted',
        align === 'right' ? 'text-right' : 'text-left',
      )}
    >
      {sortable ? (
        <button
          type="button"
          onClick={() => onSort!(sortKey!)}
          className={cn(
            'inline-flex items-center gap-1 hover:text-text-secondary',
            align === 'right' && 'flex-row-reverse',
          )}
        >
          {children}
          {isActive ? (
            activeSort?.dir === 'desc' ? (
              <ArrowDown className="size-3.5" aria-hidden="true" />
            ) : (
              <ArrowUp className="size-3.5" aria-hidden="true" />
            )
          ) : (
            <ChevronsUpDown className="size-3.5 opacity-50" aria-hidden="true" />
          )}
        </button>
      ) : (
        children
      )}
    </th>
  )
}

function Cell({
  children,
  align = 'left',
  className,
}: {
  children: React.ReactNode
  align?: 'left' | 'right'
  className?: string
}) {
  return (
    <td
      className={cn(
        'px-4 py-3 text-text-secondary',
        align === 'right' ? 'text-right' : 'text-left',
        className,
      )}
    >
      {children}
    </td>
  )
}

Table.Head = Head
Table.Body = Body
Table.Row = Row
Table.HeaderCell = HeaderCell
Table.Cell = Cell

export { Table }
```

- [ ] **Step 2: Implement `src/components/ui/Pagination.tsx`**

```tsx
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from './Button'

interface PaginationProps {
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
  onPageChange: (page: number) => void
}

export function Pagination({ page, pageSize, totalCount, totalPages, onPageChange }: PaginationProps) {
  if (totalPages <= 1) return null

  const first = (page - 1) * pageSize + 1
  const last = Math.min(page * pageSize, totalCount)

  return (
    <div className="flex items-center justify-between gap-4 pt-3 text-sm text-text-muted">
      <p>
        {first}–{last} of {totalCount}
      </p>
      <div className="flex gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPageChange(page - 1)}
          disabled={page <= 1}
          leadingIcon={<ChevronLeft className="size-4" aria-hidden="true" />}
        >
          Previous
        </Button>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => onPageChange(page + 1)}
          disabled={page >= totalPages}
          trailingIcon={<ChevronRight className="size-4" aria-hidden="true" />}
        >
          Next
        </Button>
      </div>
    </div>
  )
}
```

- [ ] **Step 3: Implement `src/components/ui/SearchInput.tsx`**

```tsx
import { Search, X } from 'lucide-react'
import { cn } from '../../lib/cn'
import { inputClass } from './TextField'

interface SearchInputProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  className?: string
}

export function SearchInput({ value, onChange, placeholder = 'Search', className }: SearchInputProps) {
  return (
    <div className={cn('relative', className)}>
      <Search
        className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
        aria-hidden="true"
      />
      <input
        type="search"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={placeholder}
        className={cn(inputClass, 'pl-9 pr-9')}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Clear search"
          className="absolute right-2.5 top-1/2 -translate-y-1/2 rounded p-1 text-text-muted hover:bg-surface-subtle"
        >
          <X className="size-4" aria-hidden="true" />
        </button>
      )}
    </div>
  )
}
```

- [ ] **Step 4: Export from the barrel**

In `src/components/ui/index.ts` add:

```ts
export { Table } from './Table'
export { Pagination } from './Pagination'
export { SearchInput } from './SearchInput'
```

- [ ] **Step 5: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors. (If `Table.Head = Head` triggers `react/only-export-components`, it is a `warn` not `error`; leave it — the pattern is idiomatic. If `oxlint` hard-errors, convert to a single `export const Table = Object.assign(TableRoot, { Head, Body, Row, HeaderCell, Cell })`.)
Run: `npm run lint` — expected: no errors.

- [ ] **Step 6: Commit**

```bash
git add src/components/ui/Table.tsx src/components/ui/Pagination.tsx src/components/ui/SearchInput.tsx src/components/ui/index.ts
git commit -m "web: Table, Pagination, SearchInput primitives"
```

---

## Task 8: Grouped nav + Sidebar + route scaffold

**Files:**
- Modify: `src/lib/nav.ts`, `src/components/layout/Sidebar.tsx`, `src/App.tsx`
- Create: `src/pages/CategoriesPage.tsx`, `src/pages/ProductsPage.tsx`, `src/pages/ProductDetailPage.tsx`, `src/pages/ProductCreatePage.tsx`, `src/pages/ProductEditPage.tsx` (each a minimal stub this task; filled in later tasks)

**Interfaces:**
- Produces:
  ```ts
  // nav.ts
  export interface NavItem { label: string; icon: LucideIcon; to?: string; enabled: boolean }
  export interface NavGroup { label?: string; items: NavItem[] }
  export const NAV_GROUPS: NavGroup[]
  ```
- Route paths (consumed by later tasks): `/categories`, `/products`, `/products/new`, `/products/:id`, `/products/:id/edit`.

- [ ] **Step 1: Rewrite `src/lib/nav.ts`**

```ts
import {
  BarChart3,
  ClipboardList,
  LayoutDashboard,
  Package,
  Settings,
  ShoppingCart,
  Tag,
  Users,
  Warehouse,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'

export interface NavItem {
  label: string
  icon: LucideIcon
  /** Present only for routes that exist today. */
  to?: string
  enabled: boolean
}

export interface NavGroup {
  /** Optional uppercase section label rendered above the group. */
  label?: string
  items: NavItem[]
}

export const NAV_GROUPS: NavGroup[] = [
  {
    items: [{ label: 'Dashboard', icon: LayoutDashboard, to: '/dashboard', enabled: true }],
  },
  {
    label: 'Catalog',
    items: [
      { label: 'Products', icon: Package, to: '/products', enabled: true },
      { label: 'Categories', icon: Tag, to: '/categories', enabled: true },
    ],
  },
  {
    items: [
      { label: 'Inventory', icon: Warehouse, enabled: false },
      { label: 'POS', icon: ShoppingCart, enabled: false },
      { label: 'Sales', icon: ClipboardList, enabled: false },
      { label: 'Reports', icon: BarChart3, enabled: false },
      { label: 'Staff', icon: Users, enabled: false },
      { label: 'Settings', icon: Settings, enabled: false },
    ],
  },
]
```

- [ ] **Step 2: Update `src/components/layout/Sidebar.tsx`**

Replace the single `NAV_ITEMS.map(...)` with a `NAV_GROUPS.map(...)` that renders an optional `<p>` group label then the same `<li>` items as today. Keep the existing enabled-vs-"Soon" rendering **exactly**. Replace the footer line:

```tsx
// was: <p className="px-3 text-xs text-text-muted">Phase 1 · SaaS foundation</p>
<p className="px-3 text-xs text-text-muted">Negosio</p>
```

Full nav block:

```tsx
<nav className="flex-1 space-y-5" aria-label="Main">
  {NAV_GROUPS.map((group, i) => (
    <div key={group.label ?? `group-${i}`}>
      {group.label && (
        <p className="px-3 pb-1.5 text-[11px] font-semibold uppercase tracking-wide text-text-muted">
          {group.label}
        </p>
      )}
      <ul className="space-y-1">
        {group.items.map((item) => {
          const Icon = item.icon
          if (!item.enabled || !item.to) {
            return (
              <li key={item.label}>
                <span
                  aria-disabled="true"
                  className="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-text-muted"
                >
                  <Icon className="size-[18px]" aria-hidden="true" />
                  <span className="flex-1">{item.label}</span>
                  <Badge tone="neutral">Soon</Badge>
                </span>
              </li>
            )
          }
          return (
            <li key={item.label}>
              <NavLink
                to={item.to}
                onClick={onNavigate}
                className={({ isActive }) =>
                  cn(
                    'relative flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
                    isActive
                      ? 'bg-primary-50 text-primary-700'
                      : 'text-text-secondary hover:bg-surface-subtle hover:text-text-primary',
                  )
                }
              >
                {({ isActive }) => (
                  <>
                    {isActive && (
                      <span
                        className="absolute inset-y-1 left-0 w-1 rounded-r bg-primary-600"
                        aria-hidden="true"
                      />
                    )}
                    <Icon
                      className={cn('size-[18px]', isActive ? 'text-primary-600' : 'text-text-muted')}
                      aria-hidden="true"
                    />
                    {item.label}
                  </>
                )}
              </NavLink>
            </li>
          )
        })}
      </ul>
    </div>
  ))}
</nav>
```

Update the import: `import { NAV_GROUPS } from '../../lib/nav'`.

- [ ] **Step 3: Create the five page stubs**

Each file, e.g. `src/pages/CategoriesPage.tsx`:

```tsx
import { DashboardLayout } from '../components/layout/DashboardLayout'

export default function CategoriesPage() {
  return <DashboardLayout title="Categories">Coming up in this build.</DashboardLayout>
}
```

Do the same for `ProductsPage` (title "Products"), `ProductDetailPage` (title "Product"), `ProductCreatePage` (title "New product"), `ProductEditPage` (title "Edit product").

- [ ] **Step 4: Wire routes in `src/App.tsx`**

```tsx
import { Navigate, Route, Routes } from 'react-router-dom'
import { ProtectedRoute } from './auth/ProtectedRoute'
import CategoriesPage from './pages/CategoriesPage'
import DashboardPage from './pages/DashboardPage'
import LoginPage from './pages/LoginPage'
import ProductCreatePage from './pages/ProductCreatePage'
import ProductDetailPage from './pages/ProductDetailPage'
import ProductEditPage from './pages/ProductEditPage'
import ProductsPage from './pages/ProductsPage'
import RegisterPage from './pages/RegisterPage'

const protectedRoutes: Array<{ path: string; element: React.ReactNode }> = [
  { path: '/dashboard', element: <DashboardPage /> },
  { path: '/products', element: <ProductsPage /> },
  { path: '/products/new', element: <ProductCreatePage /> },
  { path: '/products/:id', element: <ProductDetailPage /> },
  { path: '/products/:id/edit', element: <ProductEditPage /> },
  { path: '/categories', element: <CategoriesPage /> },
]

export default function App() {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/dashboard" replace />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/login" element={<LoginPage />} />
      {protectedRoutes.map(({ path, element }) => (
        <Route key={path} path={path} element={<ProtectedRoute>{element}</ProtectedRoute>} />
      ))}
      <Route path="*" element={<Navigate to="/dashboard" replace />} />
    </Routes>
  )
}
```

- [ ] **Step 5: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 6: Manual browser check**

`npm run dev`, log in (Owner smoke tenant), confirm: sidebar shows a **Catalog** group with **Products** + **Categories** as active links; Inventory/POS/Sales/Reports/Staff/Settings still show "Soon"; clicking Products / Categories navigates to the stub pages; footer reads "Negosio" (no phase text); Dashboard / Login / Register still work.

- [ ] **Step 7: Commit**

```bash
git add src/lib/nav.ts src/components/layout/Sidebar.tsx src/App.tsx src/pages/
git commit -m "web: grouped catalog nav + routes + page stubs"
```

---

## Task 9: Categories page — read (list, search, filter, paginate)

**Files:**
- Modify: `src/pages/CategoriesPage.tsx`

**Interfaces:**
- Consumes: `categoriesApi`, `usePagedQuery`, `useCan`, `Table`, `Pagination`, `SearchInput`, `Select`, `Badge`, `PageHeader`, `EmptyState`, `ErrorState`, `SkeletonText`, `DashboardLayout`.
- Produces: nothing new (page-internal).

- [ ] **Step 1: Implement the read-only page**

```tsx
import { useQuery } from '@tanstack/react-query'
import { Tag } from 'lucide-react'
import { categoriesApi } from '../api/catalog'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  EmptyState,
  ErrorState,
  Pagination,
  SearchInput,
  Select,
  SkeletonText,
  Table,
} from '../components/ui'

type StatusFilter = { status: string | undefined }

function isActiveParam(status: string | undefined): boolean | undefined {
  if (status === 'active') return true
  if (status === 'inactive') return false
  return undefined
}

export default function CategoriesPage() {
  const q = usePagedQuery<StatusFilter>({ defaultFilters: { status: undefined } })

  const query = useQuery({
    queryKey: ['categories', { page: q.page, search: q.search, status: q.filters.status }],
    queryFn: () =>
      categoriesApi.list({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        isActive: isActiveParam(q.filters.status),
      }),
  })

  return (
    <DashboardLayout title="Categories">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Categories</h1>
        </div>

        <div className="flex flex-col gap-3 sm:flex-row">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search categories"
          />
          <Select
            label=""
            aria-label="Status"
            className="sm:max-w-[10rem]"
            value={q.filters.status ?? ''}
            onChange={(e) => q.setFilter('status', e.target.value || undefined)}
          >
            <option value="">All statuses</option>
            <option value="active">Active</option>
            <option value="inactive">Inactive</option>
          </Select>
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            <Table.Head>
              <Table.HeaderCell>Name</Table.HeaderCell>
              <Table.HeaderCell>Description</Table.HeaderCell>
              <Table.HeaderCell align="right">Products</Table.HeaderCell>
              <Table.HeaderCell>Status</Table.HeaderCell>
            </Table.Head>
            <Table.Body>
              {Array.from({ length: 5 }).map((_, i) => (
                <Table.Row key={i}>
                  <Table.Cell><SkeletonText className="w-32" /></Table.Cell>
                  <Table.Cell><SkeletonText className="w-48" /></Table.Cell>
                  <Table.Cell align="right"><SkeletonText className="ml-auto w-8" /></Table.Cell>
                  <Table.Cell><SkeletonText className="w-16" /></Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={Tag}
            title="No categories"
            description="Categories group your products and power the product filters."
          />
        ) : (
          <>
            <Table>
              <Table.Head>
                <Table.HeaderCell>Name</Table.HeaderCell>
                <Table.HeaderCell>Description</Table.HeaderCell>
                <Table.HeaderCell align="right">Products</Table.HeaderCell>
                <Table.HeaderCell>Status</Table.HeaderCell>
              </Table.Head>
              <Table.Body>
                {query.data.items.map((c) => (
                  <Table.Row key={c.id}>
                    <Table.Cell className="font-semibold text-text-primary">{c.name}</Table.Cell>
                    <Table.Cell>
                      <span className="line-clamp-1 text-text-muted">{c.description ?? '—'}</span>
                    </Table.Cell>
                    <Table.Cell align="right">{c.productCount}</Table.Cell>
                    <Table.Cell>
                      <Badge tone={c.isActive ? 'success' : 'neutral'}>
                        {c.isActive ? 'Active' : 'Inactive'}
                      </Badge>
                    </Table.Cell>
                  </Table.Row>
                ))}
              </Table.Body>
            </Table>
            <Pagination
              page={query.data.page}
              pageSize={query.data.pageSize}
              totalCount={query.data.totalCount}
              totalPages={query.data.totalPages}
              onPageChange={q.setPage}
            />
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
```

Note: `Select`'s `label` prop is required by its current signature; passing `label=""` renders an empty label element. If that produces an empty visible `<label>` gap that looks wrong, add an optional `label?: string` to `Select` in a tiny follow-up within this task and render the `<label>` only when truthy — keep that change minimal.

- [ ] **Step 2: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 3: Manual browser check**

`npm run dev`. On `/categories` with the smoke tenant: seed ≥ 21 categories first via `curl` (loop `POST /api/categories`), then verify list renders, search narrows results and the URL gains `?q=`, status filter works and adds `?status=`, pagination shows page 2 and the URL gains `?page=2`, refreshing the page keeps all params, pressing Back after paging returns to page 1.

- [ ] **Step 4: Commit**

```bash
git add src/pages/CategoriesPage.tsx src/components/ui/Select.tsx
git commit -m "web: categories list page (search, status filter, pagination)"
```

---

## Task 10: Categories — create / edit / deactivate / reactivate

**Files:**
- Create: `src/components/catalog/CategoryFormModal.tsx`
- Modify: `src/pages/CategoriesPage.tsx`

**Interfaces:**
- Consumes: `categoriesApi`, `Modal`, `Button`, `TextField`, `ConfirmDialog`, `useToast`, `fieldErrorsFrom`, `mapCodeToField`, `ApiError`, `useMutation`, `useQueryClient`.
- Produces:
  ```ts
  export function CategoryFormModal(props: {
    open: boolean
    onClose: () => void
    category: CategoryDto | null   // null = create, else edit
  }): React.ReactNode
  ```

- [ ] **Step 1: Implement `CategoryFormModal.tsx`**

```tsx
import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { categoriesApi } from '../../api/catalog'
import { ApiError } from '../../api/client'
import type { CategoryDto } from '../../api/types'
import { fieldErrorsFrom, mapCodeToField } from '../../lib/formErrors'
import { Button, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  category: CategoryDto | null
}

export function CategoryFormModal({ open, onClose, category }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [nameError, setNameError] = useState('')

  useEffect(() => {
    if (!open) return
    setName(category?.name ?? '')
    setDescription(category?.description ?? '')
    setNameError('')
  }, [open, category])

  const mutation = useMutation({
    mutationFn: () => {
      const body = { name: name.trim(), description: description.trim() || null }
      return category
        ? categoriesApi.update(category.id, { ...body, isActive: category.isActive })
        : categoriesApi.create(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['categories'] })
      toast('success', category ? 'Changes saved' : 'Category created')
      onClose()
    },
    onError: (error) => {
      const fields = fieldErrorsFrom(error)
      const codeField = error instanceof ApiError ? mapCodeToField(error.code) : null
      if (fields.name) setNameError(fields.name)
      else if (codeField === 'name') setNameError('A category with this name already exists.')
      else toast('error', error instanceof Error ? error.message : 'Something went wrong.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    setNameError('')
    if (!name.trim()) {
      setNameError('Name is required.')
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={category ? 'Edit category' : 'New category'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {category ? 'Save' : 'Create'}
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="space-y-1">
        <TextField
          label="Name"
          name="name"
          value={name}
          maxLength={80}
          onChange={(e) => setName(e.target.value)}
          error={nameError || undefined}
          autoFocus
        />
        <label className="text-sm font-semibold text-text-secondary" htmlFor="category-description">
          Description
        </label>
        <textarea
          id="category-description"
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          rows={3}
          maxLength={500}
          className="mt-1.5 w-full rounded-lg border border-border-strong bg-white px-3 py-2 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
        />
      </form>
    </Modal>
  )
}
```

- [ ] **Step 2: Wire actions into `CategoriesPage.tsx`**

Add: `useCan`, `useMutation`/`useQueryClient`, `useState` for modal + confirm state, `Button`, `ConfirmDialog`, `CategoryFormModal`, `useToast`.

- Add a `New category` button in the header, rendered only when `useCan('catalog:write')`, that opens `CategoryFormModal` with `category={null}`.
- Add an **Actions** `Table.HeaderCell` + `Table.Cell` (only when `catalog:write`): an `Edit` `Button` (`variant="ghost" size="sm"`) opening the modal with that row; and `Deactivate` (active rows) → opens a `ConfirmDialog`, or `Reactivate` (inactive rows) → directly calls a mutation.
- Deactivate mutation: `categoriesApi.deactivate(id)`; on success invalidate `['categories']`, toast `"{name} deactivated"`, close dialog.
- Reactivate mutation: `categoriesApi.update(id, { name, description, isActive: true })`; on success invalidate + toast `"{name} reactivated"`.
- Both mutations' triggering buttons are `disabled`/`loading` while pending.
- `ConfirmDialog` copy: title "Deactivate category", message "Products keep this category, but it will be hidden from category pickers. You can reactivate it later.", `confirmLabel="Deactivate"`, `tone="danger"`, `loading={deactivateMutation.isPending}`.

- [ ] **Step 3: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 4: Manual browser check**

`/categories` as Owner: create "Beverages" and "Snacks"; edit "Snacks" description; deactivate "Snacks" (confirm dialog appears, list updates, badge → Inactive); reactivate it; try creating "Beverages" again → inline name error "A category with this name already exists."; confirm the New/Edit/Deactivate controls disappear if you point the app at a Cashier token (skip if no such user — note it).

- [ ] **Step 5: Commit**

```bash
git add src/components/catalog/CategoryFormModal.tsx src/pages/CategoriesPage.tsx
git commit -m "web: category create / edit / deactivate / reactivate"
```

---

## Task 11: Products list page

**Files:**
- Modify: `src/pages/ProductsPage.tsx`

**Interfaces:**
- Consumes: `productsApi`, `categoriesApi.listAllActive`, `usePagedQuery`, `useCan`, `formatMoney`, `formatRange`, `Table`, `Pagination`, `SearchInput`, `Select`, `Badge`, `Button`, `EmptyState`, `ErrorState`, `SkeletonText`, `Link` from `react-router-dom`.

- [ ] **Step 1: Implement the list page**

Key structure:
- `const canWrite = useCan('catalog:write')`, `const canViewCost = useCan('costs:view')`.
- `usePagedQuery<{ categoryId?: string; status?: string; tracking?: string }>({ defaultFilters: { categoryId: undefined, status: undefined, tracking: undefined } })`.
- Categories feed: `useQuery({ queryKey: ['categories', 'all-active'], queryFn: categoriesApi.listAllActive })`.
- Products query:
  ```ts
  useQuery({
    queryKey: ['products', {
      page: q.page, search: q.search, categoryId: q.filters.categoryId,
      status: q.filters.status, tracking: q.filters.tracking,
      sortBy: q.sortBy, sortDirection: q.sortDirection,
    }],
    queryFn: () => productsApi.list({
      page: q.page, pageSize: q.pageSize,
      search: q.search || undefined,
      categoryId: q.filters.categoryId,
      isActive: q.filters.status === 'active' ? true : q.filters.status === 'inactive' ? false : undefined,
      trackInventory: q.filters.tracking === 'tracked' ? true : q.filters.tracking === 'untracked' ? false : undefined,
      sortBy: (q.sortBy as ProductSortBy | undefined) ?? undefined,
      sortDirection: q.sortDirection,
    }),
  })
  ```
- Header: `<h1>Products</h1>` + (if `canWrite`) `<Button leadingIcon={<Plus/>} onClick={() => navigate('/products/new')}>New product</Button>`.
- Toolbar row: `SearchInput` (placeholder "Search name, SKU or barcode"), then three `Select`s — Category ("All categories" + `categoriesQuery.data`), Status (All/Active/Inactive), Tracking (All/Tracked/Not tracked). Each `onChange` → `q.setFilter(key, value || undefined)`.
- Table columns:
  - `Table.HeaderCell` **Product** — `sortKey="name" activeSort={{ by: q.sortBy, dir: q.sortDirection }} onSort={q.setSort}`
  - **Category**
  - `Table.HeaderCell` **Price** align right — `sortKey="sellingprice"` …
  - **Cost** align right — **render this HeaderCell and the matching cells only when `canViewCost`**
  - **Variants**
  - **Tracking**
  - **Status**
- Row cell for Product name: `<Link to={`/products/${p.id}`} className="font-semibold text-primary-700 hover:underline">{p.name}</Link>` + description `<span className="line-clamp-1 text-text-muted">`. No `onClick` on the row.
- Price cell: `p.hasVariants ? formatRange(p.minSellingPrice, p.maxSellingPrice, formatMoney) : formatMoney(p.minSellingPrice)`.
- Cost cell (only when `canViewCost`): `p.minCostPrice == null ? '—' : (p.hasVariants && p.maxCostPrice != null ? formatRange(p.minCostPrice, p.maxCostPrice, formatMoney) : formatMoney(p.minCostPrice))`.
- Variants cell: `p.hasVariants ? p.variantCount : 'Simple'`.
- Tracking cell: `p.trackInventory ? <Badge tone="blue">Tracked</Badge> : <span className="text-text-muted">—</span>`.
- Status cell: `<Badge tone={p.isActive ? 'success' : 'neutral'}>…</Badge>`.
- Loading → skeleton rows (same column count, respect `canViewCost`); error → `ErrorState`; empty → `EmptyState` (icon `Package`, title "No products", description "Create your first product to start selling.", `action` = the New product button when `canWrite`).
- `Pagination` from `data`, `onPageChange={q.setPage}`.

Import `ProductSortBy` as a type from `../api/types`.

- [ ] **Step 2: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 3: Manual browser check**

`/products` as Owner: with products seeded (use the Phase 2.5 smoke `curl` flow or the create UI once Task 14 lands — for now seed via `curl`): list renders with a Cost column; name links to `/products/:id` (row body is not clickable); search by name and by SKU both work; each filter updates the URL and resets to page 1; clicking the Product and Price headers cycles asc → desc → none and updates `?sort=`/`?dir=`; refresh restores everything; Back from a product returns to the same list state.

- [ ] **Step 4: Commit**

```bash
git add src/pages/ProductsPage.tsx
git commit -m "web: products list (search, filters, sort, pagination, cost column gating)"
```

---

## Task 12: Product detail page — view + deactivate/reactivate

**Files:**
- Modify: `src/pages/ProductDetailPage.tsx`

**Interfaces:**
- Consumes: `productsApi`, `useCan`, `formatMoney`, `formatMarginPct`, `buildUpdateProductRequest`, `ConfirmDialog`, `Badge`, `Button`, `Card`, `Table`, `EmptyState`, `ErrorState`, `LoadingState`, `useToast`, `useParams`, `Link`, `useMutation`, `useQueryClient`, `ApiError`.

- [ ] **Step 1: Implement the detail page (no variant mutations yet — Task 13)**

Structure:
- `const { id } = useParams<{ id: string }>()`.
- `const detail = useQuery({ queryKey: ['product', id], queryFn: () => productsApi.get(id!), enabled: !!id })`.
- 404 handling: `if (detail.isError && detail.error instanceof ApiError && detail.error.status === 404)` → `<DashboardLayout title="Product"><EmptyState icon={PackageX} title="Product not found" description="It may have been removed." action={<Link .../>} /></DashboardLayout>`. Other errors → `ErrorState`. Loading → `LoadingState`.
- On success, `const { product, variants } = detail.data`.
- Header: back `<Link to="/products">` ("← Products"); `<h1>{product.name}</h1>`; `<Badge tone={product.isActive ? 'success' : 'neutral'}>`. When `useCan('catalog:write')`: `Edit` `Button` → `navigate(`/products/${id}/edit`)`, and a `Deactivate`/`Reactivate` `Button`.
  - Deactivate: opens `ConfirmDialog` → mutation `productsApi.deactivate(id!)`.
  - Reactivate: mutation `productsApi.update(id!, buildUpdateProductRequest(product, { isActive: true }))`.
  - Both: `onSuccess` invalidate `['product', id]` + `['products']`, toast, close dialog. Buttons disabled while pending.
- Attributes `Card`: definition list — Category (`product.categoryName`), Description (`product.description ?? '—'`), Track inventory (`product.trackInventory ? 'Yes' : 'No'`), Created (`new Date(product.createdAtUtc).toLocaleDateString()`), Last updated (same for `updatedAtUtc`).
- Pricing / Variants `Card`:
  - `const canViewCost = useCan('costs:view')`.
  - **Simple** (`!product.hasVariants`): read-only rows — SKU (`product.sku ?? '—'`), Barcode (`product.barcode ?? '—'`), Selling price (`formatMoney(product.minSellingPrice)`), and when `canViewCost && product.minCostPrice != null`: Cost price (`formatMoney(product.minCostPrice)`) + Margin (`formatMarginPct(product.minCostPrice, product.minSellingPrice) ?? '—'`). Sub-text paragraph: "This product is sold as a single item. Add a variant to sell multiple versions (sizes, flavours, colours…)." A disabled-looking `Add variant` `Button` placeholder (wired in Task 13) — for this task render it but `onClick` = a no-op `// TODO Task 13`. Actually: **omit the Add-variant button entirely in this task** to avoid a dead control; Task 13 adds it.
  - **Has variants** (`product.hasVariants`): a `Table` of `variants`.
    - Local state `const [showInactive, setShowInactive] = useState(false)`; `const rows = showInactive ? variants : variants.filter(v => v.isActive)`.
    - A checkbox "Show inactive" above the table.
    - Columns: Variant (`v.name` + `v.isDefault` → `<Badge tone="neutral">Default</Badge>`), SKU, Barcode, Selling (`formatMoney(v.sellingPrice)`), Cost + Margin (only when `canViewCost`; `v.costPrice == null ? '—' : formatMoney(v.costPrice)` and `formatMarginPct(v.costPrice, v.sellingPrice) ?? '—'`), Status badge.
    - No row actions in this task (Task 13).

- [ ] **Step 2: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 3: Manual browser check**

Visit a simple product and a variant product (seed via `curl`): both render correctly; `/products/<bad-guid>` → "Product not found"; Deactivate shows the confirm dialog, updates the badge, and Reactivate flips it back; Cost/Margin show for Owner.

- [ ] **Step 4: Commit**

```bash
git add src/pages/ProductDetailPage.tsx
git commit -m "web: product detail view + deactivate/reactivate"
```

---

## Task 13: Variant add / edit / deactivate

**Files:**
- Create: `src/components/catalog/VariantFormModal.tsx`
- Modify: `src/pages/ProductDetailPage.tsx`

**Interfaces:**
- Produces:
  ```ts
  export function VariantFormModal(props: {
    open: boolean
    onClose: () => void
    productId: string
    variant: ProductVariantDto | null   // null = add
  }): React.ReactNode
  ```

- [ ] **Step 1: Implement `VariantFormModal.tsx`**

Mirror `CategoryFormModal` structure. Fields (all in local state, seeded from `variant` on open):
- Name — `TextField`, required.
- SKU — `TextField`, optional.
- Barcode — `TextField`, optional.
- Cost price — `TextField type="number"` `min={0}` `step="0.01"`, default `'0'`.
- Selling price — `TextField type="number"` `min={0}` `step="0.01"`, default `'0'`.

`mutationFn`: build `VariantInput` = `{ name: name.trim(), sku: sku.trim() || null, barcode: barcode.trim() || null, costPrice: Number(cost) || 0, sellingPrice: Number(selling) || 0 }`; call `variant ? variantsApi.update(productId, variant.id, body) : variantsApi.create(productId, body)`.
`onSuccess`: `qc.invalidateQueries({ queryKey: ['product', productId] })` + `qc.invalidateQueries({ queryKey: ['products'] })` + toast + close.
`onError`: `fieldErrorsFrom` + `mapCodeToField` → attach `SKU_ALREADY_EXISTS`/`BARCODE_ALREADY_EXISTS` to the sku/barcode field; else toast `error.message`.
Client validation: name required, cost ≥ 0, selling ≥ 0.
Confirm/submit button disabled + `loading` while pending.

- [ ] **Step 2: Wire into `ProductDetailPage.tsx`**

- State: `const [variantModal, setVariantModal] = useState<{ open: boolean; variant: ProductVariantDto | null }>({ open: false, variant: null })`.
- Add `Add variant` `Button` (gated on `catalog:write`) in **both** the simple-product block and the has-variants block → `setVariantModal({ open: true, variant: null })`.
- In the has-variants table, add a gated **Actions** column: `Edit` (`variant="ghost" size="sm"`) → `setVariantModal({ open: true, variant: v })`; `Deactivate` → opens a `ConfirmDialog` bound to `v`.
- Deactivate mutation: `variantsApi.deactivate(productId, v.id)`; `onSuccess` invalidate `['product', id]` + `['products']` + toast `"{name} deactivated"`; `onError` → toast `error.message` (the backend owns the "can't remove the last active / default" rule — surface whatever it says; do not pre-check).
- Render `<VariantFormModal open={variantModal.open} onClose={() => setVariantModal({ open: false, variant: null })} productId={id!} variant={variantModal.variant} />`.

- [ ] **Step 3: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 4: Manual browser check**

On a simple product: `Add variant` → the card switches to the variants table (product now `hasVariants`). On a variant product: add a 2nd/3rd variant; edit one; deactivate one and confirm the list updates; toggle "Show inactive"; try to deactivate the last active variant and confirm the backend error surfaces as a toast (do not crash).

- [ ] **Step 5: Commit**

```bash
git add src/components/catalog/VariantFormModal.tsx src/pages/ProductDetailPage.tsx
git commit -m "web: variant add / edit / deactivate on product detail"
```

---

## Task 14: Product create form + page

**Files:**
- Create: `src/components/catalog/ProductFields.tsx`, `src/components/catalog/VariantRowsField.tsx`, `src/components/catalog/ProductCreateForm.tsx`
- Modify: `src/pages/ProductCreatePage.tsx`

**Interfaces:**
- Produces:
  ```ts
  // ProductFields.tsx — the shared "Details" group (create + edit)
  export interface ProductDetailsValue {
    categoryId: string
    name: string
    description: string
    trackInventory: boolean
  }
  export function ProductFields(props: {
    value: ProductDetailsValue
    onChange: (value: ProductDetailsValue) => void
    categories: CategoryDto[]
    errors: Record<string, string>
  }): React.ReactNode

  // VariantRowsField.tsx
  export interface VariantRow { name: string; sku: string; barcode: string; costPrice: string; sellingPrice: string }
  export function VariantRowsField(props: {
    rows: VariantRow[]
    onChange: (rows: VariantRow[]) => void
  }): React.ReactNode

  // ProductCreateForm.tsx
  export function ProductCreateForm(props: { categories: CategoryDto[] }): React.ReactNode
  ```

- [ ] **Step 1: Implement `ProductFields.tsx`**

A field group: Category `Select` (options from `categories`; first option `<option value="">Select a category</option>`), Name `TextField` (`maxLength={120}`), Description `<textarea>`, and a Track-inventory toggle (`role="switch"` button or a styled checkbox) with hint "Deduct stock on sale and show this product in inventory." Wire each control to call `onChange({ ...value, <field>: … })`. Surface `errors.categoryid` / `errors.name` on the respective fields.

- [ ] **Step 2: Implement `VariantRowsField.tsx`**

Renders `rows.map(...)`, each row a bordered block with `TextField`s: Name (required marker), SKU, Barcode, Cost price (`type="number" min={0} step="0.01"`), Selling price (same). A "Remove" `Button` (`variant="ghost" size="sm"`, `disabled={rows.length <= 1}`). Below: an "Add variant" `Button` that appends `{ name: '', sku: '', barcode: '', costPrice: '0', sellingPrice: '0' }`.

- [ ] **Step 3: Implement `ProductCreateForm.tsx`**

- Local state: `details: ProductDetailsValue` (`{ categoryId: '', name: '', description: '', trackInventory: true }`), `mode: 'single' | 'variants'` (default `'single'`), single fields `{ sku, barcode, costPrice, sellingPrice }` (strings, prices default `'0'`), `rows: VariantRow[]` (default one empty row), `errors: Record<string,string>`.
- If `categories.length === 0`: render a `Callout` "Create a category first" + `<Link to="/categories">` and disable the submit button.
- Segmented control: two buttons "Single item" / "Has variants" toggling `mode`.
- `mode === 'single'` → SKU, Barcode, Cost price, Selling price `TextField`s. `mode === 'variants'` → `<VariantRowsField rows={rows} onChange={setRows} />`.
- Submit `mutation`:
  ```ts
  mutationFn: () => {
    const base = {
      categoryId: details.categoryId, name: details.name.trim(),
      description: details.description.trim() || null, trackInventory: details.trackInventory,
    }
    const body: CreateProductRequest =
      mode === 'variants'
        ? { ...base, sku: null, barcode: null, costPrice: 0, sellingPrice: 0,
            variants: rows.map((r) => ({
              name: r.name.trim(), sku: r.sku.trim() || null, barcode: r.barcode.trim() || null,
              costPrice: Number(r.costPrice) || 0, sellingPrice: Number(r.sellingPrice) || 0,
            })) }
        : { ...base, sku: single.sku.trim() || null, barcode: single.barcode.trim() || null,
            costPrice: Number(single.costPrice) || 0, sellingPrice: Number(single.sellingPrice) || 0,
            variants: null }
    return productsApi.create(body)
  },
  onSuccess: (detail) => {
    qc.invalidateQueries({ queryKey: ['products'] })
    toast('success', 'Product created')
    navigate(`/products/${detail.product.id}`)
  },
  onError: (error) => { setErrors(fieldErrorsFrom(error)); /* + mapCodeToField -> sku/barcode/name; else toast */ },
  ```
- Client validation before `mutate()`: `details.categoryId` non-empty (`errors.categoryid = 'Category is required.'`), `details.name.trim()` non-empty, prices ≥ 0, and in `variants` mode every row has a name.
- Submit bar: `Cancel` `Button` (`variant="secondary"`, → `navigate('/products')`) + `Create product` `Button` (`loading={mutation.isPending}`).

- [ ] **Step 4: Implement `ProductCreatePage.tsx`**

```tsx
import { useQuery } from '@tanstack/react-query'
import { Navigate } from 'react-router-dom'
import { categoriesApi } from '../api/catalog'
import { useCan } from '../lib/useCan'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ProductCreateForm } from '../components/catalog/ProductCreateForm'
import { ErrorState, LoadingState } from '../components/ui'

export default function ProductCreatePage() {
  const canWrite = useCan('catalog:write')
  const categories = useQuery({ queryKey: ['categories', 'all-active'], queryFn: categoriesApi.listAllActive })

  if (!canWrite) return <Navigate to="/products" replace />

  return (
    <DashboardLayout title="New product">
      <div className="mx-auto max-w-2xl">
        {categories.isError ? (
          <ErrorState message={(categories.error as Error).message} onRetry={() => categories.refetch()} />
        ) : categories.isPending ? (
          <LoadingState />
        ) : (
          <ProductCreateForm categories={categories.data} />
        )}
      </div>
    </DashboardLayout>
  )
}
```

- [ ] **Step 5: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 6: Manual browser check**

`/products/new`: create a simple product (with tracking) → lands on its detail page; create a product with 2 variants → detail shows the variants table; a duplicate SKU → inline field error; with no categories (fresh tenant) the form shows the "create a category first" notice and submit is disabled.

- [ ] **Step 7: Commit**

```bash
git add src/components/catalog/ProductFields.tsx src/components/catalog/VariantRowsField.tsx src/components/catalog/ProductCreateForm.tsx src/pages/ProductCreatePage.tsx
git commit -m "web: product create form (single item + variants)"
```

---

## Task 15: Product edit form + page

**Files:**
- Create: `src/components/catalog/ProductEditForm.tsx`
- Modify: `src/pages/ProductEditPage.tsx`

**Interfaces:**
- Produces:
  ```ts
  export function ProductEditForm(props: { product: ProductDto; categories: CategoryDto[] }): React.ReactNode
  ```

- [ ] **Step 1: Implement `ProductEditForm.tsx`**

- Local state seeded from `product`: `details: ProductDetailsValue` (`{ categoryId, name, description: description ?? '', trackInventory }`), `isActive` (bool), and — **only for simple products** — `single: { sku, barcode, costPrice, sellingPrice }` (strings; `sku ?? ''`, `barcode ?? ''`, `String(minCostPrice ?? 0)`, `String(minSellingPrice)`).
- Reuse `<ProductFields value={details} onChange={setDetails} categories={categoriesWithCurrent} errors={errors} />`. `categoriesWithCurrent` = active categories plus, if the product's current `categoryId` is not among them, a synthetic `{ id: product.categoryId, name: product.categoryName, ... }` so the select can represent the value.
- Active toggle (`role="switch"`).
- Simple product → render SKU / Barcode / Cost price / Selling price `TextField`s. Variant product → a muted note: "Pricing and codes are managed per variant on the product page." (no price fields).
- Submit `mutation`:
  ```ts
  mutationFn: () =>
    productsApi.update(
      product.id,
      buildUpdateProductRequest(product, {
        categoryId: details.categoryId,
        name: details.name.trim(),
        description: details.description.trim() || null,
        trackInventory: details.trackInventory,
        isActive,
        ...(product.hasVariants
          ? {}
          : {
              sku: single.sku.trim() || null,
              barcode: single.barcode.trim() || null,
              costPrice: Number(single.costPrice) || 0,
              sellingPrice: Number(single.sellingPrice) || 0,
            }),
      }),
    ),
  onSuccess: (detail) => {
    qc.invalidateQueries({ queryKey: ['products'] })
    qc.invalidateQueries({ queryKey: ['product', product.id] })
    toast('success', 'Changes saved')
    navigate(`/products/${detail.product.id}`)
  },
  onError: (error) => { setErrors(fieldErrorsFrom(error)); /* mapCodeToField -> field; else toast */ },
  ```
- Client validation: category + name non-empty; simple-product prices ≥ 0.
- Submit bar: `Cancel` → `navigate(`/products/${product.id}`)`; `Save changes` (`loading`).

- [ ] **Step 2: Implement `ProductEditPage.tsx`**

```tsx
import { useQuery } from '@tanstack/react-query'
import { Navigate, useParams } from 'react-router-dom'
import { categoriesApi, productsApi } from '../api/catalog'
import { ApiError } from '../api/client'
import { useCan } from '../lib/useCan'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ProductEditForm } from '../components/catalog/ProductEditForm'
import { EmptyState, ErrorState, LoadingState } from '../components/ui'

export default function ProductEditPage() {
  const { id } = useParams<{ id: string }>()
  const canWrite = useCan('catalog:write')
  const product = useQuery({ queryKey: ['product', id], queryFn: () => productsApi.get(id!), enabled: !!id })
  const categories = useQuery({ queryKey: ['categories', 'all-active'], queryFn: categoriesApi.listAllActive })

  if (!canWrite) return <Navigate to={`/products/${id}`} replace />

  return (
    <DashboardLayout title="Edit product">
      <div className="mx-auto max-w-2xl">
        {product.isError && product.error instanceof ApiError && product.error.status === 404 ? (
          <EmptyState title="Product not found" description="It may have been removed." />
        ) : product.isError ? (
          <ErrorState message={(product.error as Error).message} onRetry={() => product.refetch()} />
        ) : product.isPending || categories.isPending ? (
          <LoadingState />
        ) : categories.isError ? (
          <ErrorState message={(categories.error as Error).message} onRetry={() => categories.refetch()} />
        ) : (
          <ProductEditForm product={product.data.product} categories={categories.data} />
        )}
      </div>
    </DashboardLayout>
  )
}
```

- [ ] **Step 3: Typecheck + lint**

Run: `npx tsc -b` — expected: no errors.
Run: `npm run lint` — expected: no errors.

- [ ] **Step 4: Manual browser check**

Edit a simple product: change name, category, price, toggle Active off then on → detail reflects it. Edit a variant product: the price fields are absent, the note shows, changing name/category/tracking/active saves, and (verify in `sqlcmd` or by re-opening the product) the variant prices are **unchanged**.

- [ ] **Step 5: Commit**

```bash
git add src/components/catalog/ProductEditForm.tsx src/pages/ProductEditPage.tsx
git commit -m "web: product edit form (shared fields; safe update for variant products)"
```

---

## Task 16: Full verification pass + memory update

**Files:**
- Modify: `C:\Users\Ace\.claude\projects\c--Users-Ace-Documents-Negosio\memory\MEMORY.md` and a new memory file (see Step 4)

- [ ] **Step 1: Clean build + lint**

Run: `npm run lint` — expected: clean.
Run: `npm run build` — expected: `tsc -b` + `vite build` succeed, no errors.

- [ ] **Step 2: Full browser walkthrough (headless, screenshots)**

Start the API (`dotnet run --project src/Negosio.Api` from repo root) and `npm run dev`. Using the headless-Chrome CDP recipe (see `memory/visual-verify-headless-chrome.md`), drive as an Owner:

1. **Categories:** create "Beverages" + "Snacks"; edit; deactivate + reactivate; search; status filter; seed > 20 via curl and page to page 2.
2. **Products:** create a **simple** tracked product → detail. Create a **variant** product (2 variants) → detail shows variants table. Edit each. On the simple one, `Add variant` → it converts to a variant product. Deactivate / reactivate a product. Search by name and by SKU. Filter by category / status / tracking. Sort by name and by price. Paginate.
3. **Cost column** renders for the Owner.
4. **404:** `/products/<bad-guid>` → not-found state.
5. Screenshots: categories list, products list, product detail (simple), product detail (variants), create form (single mode), create form (variants mode).

- [ ] **Step 3: `Modal` + `usePagedQuery` explicit manual checks**

- Modal: open a form modal — Tab cycles within, Shift+Tab wraps back, Esc closes, backdrop closes, focus starts inside, focus returns to opener on close, background does not scroll.
- `usePagedQuery`: set search + a filter + a sort + go to page 2 → (a) refresh restores all of it from the URL; (b) open a product then Back → list restored; (c) URL query string reflects every param; (d) changing search / filter / sort each resets to page 1.

- [ ] **Step 4: Update memory**

Create `C:\Users\Ace\.claude\projects\c--Users-Ace-Documents-Negosio\memory\catalog-frontend.md`:

```markdown
---
name: catalog-frontend
description: Negosio web — Catalog UI (categories/products/variants) + the shared frontend foundation it introduced
metadata:
  type: project
---

Sub-project 1 of the frontend catch-up (frontend was 2 backend phases behind). Built the
Catalog UI + reusable foundation. Spec: `docs/superpowers/specs/2026-08-30-catalog-frontend-design.md`;
plan: `docs/superpowers/plans/2026-08-30-catalog-frontend.md`.

**Foundation added (reused by later slices):**
- `src/api/catalog.ts` (categoriesApi/productsApi/variantsApi), `src/api/query-string.ts` (`qs`).
- `src/hooks/usePagedQuery.ts` — list state (page/`q`/`sort`/`dir`/filters) synced to the URL via
  `useSearchParams`; any search/filter/sort change resets page to 1. `useDebouncedValue` (350 ms).
- `src/lib/useCan.ts` — `'catalog:write'` (Owner/Admin/Manager), `'costs:view'` (+InventoryStaff);
  UX only, mirrors `CatalogAccess.cs`.
- `src/lib/format.ts` (`formatMoney` = PHP hard-coded this phase, `formatRange`, `formatMarginPct`),
  `src/lib/formErrors.ts` (`fieldErrorsFrom`/`mapCodeToField`), `src/lib/catalogRequests.ts`
  (`buildUpdateProductRequest` — the ONE UpdateProductRequest builder; forces sku/barcode/cost/selling
  inert for variant products, which `ProductService.UpdateAsync` provably ignores).
- `components/ui`: `Modal`, `ConfirmDialog`, `Table` (+`.HeaderCell` sortable), `Pagination`, `SearchInput`.
- `lib/nav.ts` is now `NAV_GROUPS: NavGroup[]` (grouped sidebar). Sidebar footer is neutral ("Negosio"),
  no phase text.

**Catalog pages:** `/categories` (modal create/edit, deactivate/reactivate), `/products` (list;
name is the only link, no row nav), `/products/:id` (detail + variant table + add/edit/deactivate via
`VariantFormModal`), `/products/new` (`ProductCreateForm`, single-item | has-variants), `/products/:id/edit`
(`ProductEditForm`; variant products hide price fields).

**No frontend test runner** — verification is `npm run lint` + `npm run build` + manual headless
browser walkthrough. Next slice: Inventory + Tax settings.

Related: [[phase-2-catalog-inventory]] [[visual-verify-headless-chrome]]
```

Add to `MEMORY.md` under the index list:

```markdown
- [Catalog frontend](catalog-frontend.md) — Catalog UI + the shared web foundation (usePagedQuery, useCan, Table/Modal, api modules)
```

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/plans/2026-08-30-catalog-frontend.md
git commit -m "web: catalog frontend — verification pass complete"
```

(The memory files live outside the repo and are saved by the Write tool, not committed.)

---

## Self-Review

**1. Spec coverage**

| Spec section | Task(s) |
| --- | --- |
| §3.1 API modules | 2 |
| §3.2 types + `PagedResult` + DashboardResponse fix | 1 |
| §3.3 `usePagedQuery` / `useDebouncedValue` + URL sync + page reset | 5 (verified 11, 16) |
| §3.4 `useCan` | 4 |
| §3.5 `Table` / `Pagination` / `SearchInput` / `Modal` / `ConfirmDialog` | 6, 7 |
| §3.6 routes, grouped nav, Sidebar, neutral footer | 8 |
| §4.1 Categories page (read) | 9 |
| §4.1 Categories create/edit/deactivate/reactivate | 10 |
| §4.2 Products list (search/filters/sort/pagination, cost gating, name-only link) | 11 |
| §4.3 Product detail (simple + variants view, deactivate/reactivate) | 12 |
| §4.3 `VariantFormModal` add/edit/deactivate | 13 |
| §4.4 Product create (single + variants) | 14 |
| §4.5 Product edit (safe update for variant products) | 15 |
| §5 `formatMoney`/`formatRange`/`formatMarginPct` | 3 |
| §5 `formErrors` | 3 |
| §5 `buildUpdateProductRequest` (edit + reactivate share it) | 3 (used 12, 15) |
| §5 toasts / query invalidation | 10, 12, 13, 14, 15 |
| §5 404 handling | 12, 15 |
| §5 accessibility (Modal trap, Table aria-sort, icon aria-labels) | 6, 7 |
| §7 verification (lint, build, browser, Modal + history checks) | 16 (per-task checks throughout) |
| §8 `ACTIVE_CATEGORY_FETCH_LIMIT` named constant | 2 |
| §8 PHP-only currency documented | 3 |
| §8 `ProductForm` split | File Structure note + 14/15 |

No gaps.

**2. Placeholder scan**

- Task 12 Step 1 originally had a "TODO Task 13" no-op button — resolved by omitting the Add-variant button until Task 13.
- All code steps contain real code. Page tasks (11, 12, 15) give exact query keys, exact API calls, exact column lists, exact copy, and exact state shapes rather than full JSX — acceptable because every referenced symbol is defined (in earlier tasks or the existing `components/ui` barrel, whose signatures were read into the spec) and the structure is unambiguous. No "add error handling" hand-waving: each page names its loading / error / empty / 404 branch explicitly.

**3. Type consistency**

- `PagedResult<T>` shape identical in Task 1 and consumed in 2, 9, 11.
- `usePagedQuery` return shape (`page`, `pageSize`, `search`, `searchInput`, `setSearchInput`, `filters`, `setFilter`, `sortBy`, `sortDirection`, `setSort`, `setPage`) defined in Task 5, used verbatim in 9 and 11.
- `buildUpdateProductRequest(product, patch)` signature defined in Task 3, called with exactly that shape in Tasks 12 and 15.
- `ProductDetailsValue` defined in Task 14, reused in Task 15.
- `categoriesApi.listAllActive()` defined in Task 2, used in 11, 14, 15.
- `Table.HeaderCell` `sortKey` / `activeSort` / `onSort` props defined in Task 7, used in Task 11 with `activeSort={{ by: q.sortBy, dir: q.sortDirection }}`.
- Query keys consistent: `['categories', …]`, `['categories', 'all-active']`, `['products', …]`, `['product', id]` across Tasks 9–15.

No inconsistencies found.
