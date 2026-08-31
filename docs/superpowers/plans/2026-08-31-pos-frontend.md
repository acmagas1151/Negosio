# POS Frontend (Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the browser UI for the Phase 3 retail POS backend — registers, cash sessions, a full-screen checkout terminal, receipts, sales history, and returns.

**Architecture:** React 19 + TypeScript + Vite + React Router 7 + TanStack Query v5, reusing the existing Negosio shared design system (`Modal`, `Table`, `Pagination`, `SearchInput`, `Select`, `Button`, `Callout`, `useToast`, `usePagedQuery`, `useCan`). Server data lives in TanStack Query; the POS cart lives in a `useReducer` hook mirrored to `localStorage`. No Redux/Zustand. The `/pos` and `/pos/complete/:saleId` routes render their own full-screen shell outside `DashboardLayout`; everything else is a normal in-dashboard page. The backend re-prices and re-totals every checkout — the client only ever shows a preview and then trusts the server response.

**Tech Stack:** React 19, TypeScript (strict), Vite 8, React Router 7, TanStack Query v5, Tailwind v4, lucide-react, oxlint. Backend: .NET 9 API already built and tested — **no backend changes**.

**Spec:** `docs/superpowers/specs/2026-08-31-pos-frontend-design.md`

## Global Constraints

- **No backend changes.** Every screen maps to an existing endpoint (spec §2). If a task seems to need a backend change, stop and escalate.
- **No frontend test runner exists** (deliberate). Each task's verification cycle is: `cd web/negosio-web && npm run lint && npm run build` must both pass clean. Interactive tasks additionally get a headless-Chrome/CDP smoke per `memory/visual-verify-headless-chrome.md` (Chrome at `C:/Program Files/Google/Chrome/Application/chrome.exe`; run node driver scripts with `MSYS_NO_PATHCONV=1`; token storage key is `negosio.accessToken`).
- **No Redux / Zustand / new state libraries.** New deps require escalation.
- **Reuse shared components** from `src/components/ui/` — do not reinvent `Modal`, `Table`, etc.
- **`usePagedQuery` `defaultFilters` MUST be a module-level `const`** (the hook memoizes on it by reference).
- **Never compute authoritative money/stock client-side.** `saleMath.ts` is preview-only; `SaleResultDto` / `SaleDetailDto` / `ReceiptDto` from the server are the source of truth. On any mismatch, show server values, no reconciliation.
- **Never show cost data to roles outside `costs:view`** (Owner/Admin/Manager/InventoryStaff) — always null-check the API field. (POS/Sales screens in this plan show no cost data at all except `SaleItemDto.costPriceSnapshot`, which the backend already redacts to `null`.)
- **Commits:** conventional-commit messages, **no** "Co-Authored-By: Claude" / "Generated with Claude Code" trailer (repo convention). Work stays on branch `feature/pos-frontend`; do not merge to `master`.
- **Currency** is hard-coded PHP — use `formatMoney` / `formatQty` from `src/lib/format.ts`.
- **Enums cross the wire as strings** (ASP.NET is configured for string enum serialization — confirmed by existing `InventoryStatus`/`StockMovementType` unions). Model every enum as a TS string union.
- Money is `decimal(18,2)`, quantity `decimal(18,3)` — inputs use `step="0.01"` / `step="0.001"` respectively.

---

## File Structure

**New API / lib:**
- `src/api/pos.ts` — `registersApi`, `sessionsApi`, `posCatalogApi`, `checkoutApi`, `salesApi`
- `src/api/types.ts` — *(modify)* add all POS/sales/receipt/return DTOs + enums
- `src/lib/useCan.ts` — *(modify)* add `register:manage`, `pos:operate`, `sales:view`, `refund:manage`
- `src/lib/pos.ts` — label maps, `saleStatusTone`, `suggestCashButtons`
- `src/lib/saleMath.ts` — preview-only line/total math
- `src/lib/nav.ts` — *(modify)* add "Point of sale" group
- `src/App.tsx` — *(modify)* routes

**Registers:**
- `src/pages/RegistersPage.tsx`
- `src/components/registers/RegisterFormModal.tsx`
- `src/components/registers/RegisterSessionCell.tsx`

**Sessions (shared by Registers + POS):**
- `src/components/pos/OpenSessionModal.tsx`
- `src/components/pos/CloseSessionModal.tsx`

**POS terminal:**
- `src/pages/PosPage.tsx` — routing shell + state machine
- `src/pages/PosCompletePage.tsx`
- `src/components/pos/PosShell.tsx` — full-screen chrome (`PosTopBar` inlined)
- `src/components/pos/RegisterPicker.tsx`
- `src/components/pos/PosSessionGate.tsx`
- `src/components/pos/PosTerminal.tsx` — 62/38 layout, owns cart + checkout attempt
- `src/components/pos/PosSearchBar.tsx`
- `src/components/pos/PosProductGrid.tsx`
- `src/components/pos/CartPanel.tsx` — `CartList` + `CartLineRow` + `CartSummary` inlined
- `src/components/pos/LineDiscountPopover.tsx`
- `src/components/pos/PaymentModal.tsx`
- `src/hooks/usePosCart.ts`
- `src/hooks/useBarcodeScanner.ts`
- `src/lib/posStorage.ts` — scoped `localStorage` keys + prune

**Sales / receipt / returns:**
- `src/pages/SalesPage.tsx`
- `src/pages/SaleDetailPage.tsx`
- `src/pages/ReceiptPage.tsx`
- `src/components/sales/SaleItemsTable.tsx`
- `src/components/sales/SaleReturnsList.tsx`
- `src/components/sales/ReturnModal.tsx`
- `src/components/sales/StatusBadge.tsx`

---

## Task 1: Types, API module, RBAC, nav, routes

**Files:**
- Modify: `src/api/types.ts` (append POS section)
- Create: `src/api/pos.ts`
- Modify: `src/lib/useCan.ts`
- Create: `src/lib/pos.ts`
- Modify: `src/lib/nav.ts`
- Modify: `src/App.tsx`

**Interfaces:**
- Consumes: `apiRequest` from `src/api/client.ts`, `qs` from `src/api/query-string.ts`, `PagedResult<T>` from `src/api/types.ts`, `useAuth` from `src/auth/AuthContext.tsx`.
- Produces (relied on by every later task):
  - Enums: `PaymentMethod`, `DiscountType`, `SaleStatus`, `RegisterSessionStatus` (string unions).
  - `registersApi.list(params: RegisterListParams): Promise<PagedResult<RegisterDto>>`, `.get(id): Promise<RegisterDto>`, `.create(body: CreateRegisterRequest): Promise<RegisterDto>`, `.update(id, body: UpdateRegisterRequest): Promise<RegisterDto>`, `.deactivate(id): Promise<void>`
  - `sessionsApi.open(body: OpenRegisterSessionRequest): Promise<RegisterSessionDto>`, `.current(params: { registerId?: string; branchId?: string }): Promise<RegisterSessionDto>`, `.close(id: string, body: CloseRegisterSessionRequest): Promise<RegisterSessionDto>`
  - `posCatalogApi.search(params: PosCatalogParams): Promise<PagedResult<PosCatalogItemDto>>`, `.barcode(code: string, branchId: string): Promise<PosCatalogItemDto>`
  - `checkoutApi.checkout(body: CheckoutRequest): Promise<SaleResultDto>`
  - `salesApi.list(params: SaleListParams): Promise<PagedResult<SaleSummaryDto>>`, `.get(id): Promise<SaleDetailDto>`, `.receipt(id): Promise<ReceiptDto>`, `.listReturns(id): Promise<SaleReturnDto[]>`, `.createReturn(id, body: CreateReturnRequest): Promise<SaleReturnDto>`
  - `useCan('register:manage' | 'pos:operate' | 'sales:view' | 'refund:manage')`
  - `PAYMENT_METHOD_LABELS: Record<PaymentMethod, string>`, `SALE_STATUS_LABELS: Record<SaleStatus, string>`, `saleStatusTone(s: SaleStatus): 'neutral'|'success'|'warning'|'danger'`, `suggestCashButtons(total: number): number[]`

- [ ] **Step 1: Append the POS type section to `src/api/types.ts`**

```ts
// ---- POS: registers & sessions ----

export type RegisterSessionStatus = 'Open' | 'Closed'

export interface RegisterDto {
  id: string
  branchId: string
  branchName: string
  name: string
  code: string
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export interface CreateRegisterRequest {
  branchId: string
  name: string
  code: string
}

export interface UpdateRegisterRequest {
  name: string
  code: string
  isActive: boolean
}

export interface RegisterListParams {
  branchId?: string
  isActive?: boolean
  page?: number
  pageSize?: number
}

export interface RegisterSessionDto {
  id: string
  branchId: string
  registerId: string
  registerName: string
  status: RegisterSessionStatus
  openedByUserId: string
  openedByName: string
  openedAtUtc: string
  closedAtUtc: string | null
  openingCash: number
  closingCash: number | null
  expectedCash: number | null
  cashDifference: number | null
}

export interface OpenRegisterSessionRequest {
  registerId: string
  openingCash: number
}

export interface CloseRegisterSessionRequest {
  closingCash: number
}

// ---- POS: catalog & checkout ----

export type PaymentMethod = 'Cash' | 'Card' | 'GCash' | 'Maya' | 'BankTransfer' | 'Other'
export type DiscountType = 'None' | 'FixedAmount' | 'Percentage'

export interface PosCatalogItemDto {
  productId: string
  productVariantId: string
  productName: string
  variantName: string | null
  sku: string | null
  barcode: string | null
  sellingPrice: number
  quantityAvailable: number
  trackInventory: boolean
  isAvailable: boolean
}

export interface PosCatalogParams {
  branchId: string
  search?: string
  page?: number
  pageSize?: number
}

export interface CheckoutDiscountInput {
  type: DiscountType
  value: number
}

export interface CheckoutItemInput {
  productVariantId: string
  quantity: number
  discount: CheckoutDiscountInput | null
}

export interface CheckoutPaymentInput {
  method: PaymentMethod
  receivedAmount?: number | null
  amount?: number | null
  referenceNumber?: string | null
}

export interface CheckoutRequest {
  branchId: string
  registerSessionId: string
  clientRequestId: string
  items: CheckoutItemInput[]
  payments: CheckoutPaymentInput[]
}

export interface SaleResultDto {
  saleId: string
  saleNumber: string
  status: SaleStatus
  subtotal: number
  discountTotal: number
  taxTotal: number
  grandTotal: number
  amountPaid: number
  changeDue: number
  wasExistingRequest: boolean
}

// ---- Sales history & detail ----

export type SaleStatus = 'Completed' | 'Voided' | 'Refunded' | 'PartiallyRefunded'

export interface SaleItemDto {
  id: string
  productVariantId: string
  productName: string
  variantName: string | null
  sku: string | null
  barcode: string | null
  unitPrice: number
  quantity: number
  grossAmount: number
  discountAmount: number
  taxAmount: number
  netAmount: number
  costPriceSnapshot: number | null
  returnedQuantity: number
}

export interface SalePaymentDto {
  id: string
  method: PaymentMethod
  amount: number
  referenceNumber: string | null
  receivedAmount: number | null
  changeAmount: number | null
}

export interface SaleSummaryDto {
  id: string
  saleNumber: string
  branchId: string
  branchName: string
  cashierUserId: string
  cashierName: string
  itemCount: number
  grandTotal: number
  status: SaleStatus
  paymentSummary: string
  createdAtUtc: string
}

export interface SaleReturnItemDto {
  id: string
  saleItemId: string
  productVariantId: string
  productName: string
  quantity: number
  refundAmount: number
  restocked: boolean
}

export interface ReceiptPaymentDto {
  method: string
  amount: number
}

export interface SaleReturnDto {
  id: string
  returnNumber: string
  saleId: string
  reason: string
  totalRefund: number
  createdByUserId: string
  createdByName: string
  createdAtUtc: string
  items: SaleReturnItemDto[]
  refunds: ReceiptPaymentDto[]
}

export interface SaleDetailDto {
  sale: SaleSummaryDto
  registerSessionId: string
  subtotal: number
  discountTotal: number
  taxTotal: number
  amountPaid: number
  changeDue: number
  completedAtUtc: string | null
  items: SaleItemDto[]
  payments: SalePaymentDto[]
  returns: SaleReturnDto[]
}

export interface SaleListParams {
  branchId?: string
  registerId?: string
  cashierUserId?: string
  status?: SaleStatus
  fromUtc?: string
  toUtc?: string
  search?: string
  page?: number
  pageSize?: number
}

// ---- Receipt ----

export interface ReceiptLineDto {
  description: string
  variantName: string | null
  quantity: number
  unitPrice: number
  netAmount: number
}

export interface ReceiptDto {
  storeName: string
  branchName: string
  registerName: string
  saleNumber: string
  cashierName: string
  createdAtUtc: string
  lines: ReceiptLineDto[]
  subtotal: number
  discountTotal: number
  taxTotal: number
  grandTotal: number
  payments: ReceiptPaymentDto[]
  changeDue: number
  status: SaleStatus
}

// ---- Returns ----

export interface ReturnLineInput {
  saleItemId: string
  quantity: number
  restock: boolean
}

export interface CreateReturnRequest {
  items: ReturnLineInput[]
  reason: string
  refundMethod: PaymentMethod
  refundReference: string | null
}

// ---- Tax settings (read-only feed for the cart preview; NOT the settings page) ----

export interface TaxSettingsDto {
  taxRatePercent: number
  pricesIncludeTax: boolean
}
```

- [ ] **Step 2: Create `src/api/pos.ts`**

```ts
import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  CheckoutRequest,
  CreateRegisterRequest,
  CreateReturnRequest,
  CloseRegisterSessionRequest,
  OpenRegisterSessionRequest,
  PagedResult,
  PosCatalogItemDto,
  PosCatalogParams,
  RegisterDto,
  RegisterListParams,
  RegisterSessionDto,
  SaleDetailDto,
  SaleListParams,
  SaleResultDto,
  SaleReturnDto,
  SaleSummaryDto,
  ReceiptDto,
  TaxSettingsDto,
  UpdateRegisterRequest,
} from './types'

export const registersApi = {
  list: (params: RegisterListParams) =>
    apiRequest<PagedResult<RegisterDto>>(`/api/registers${qs({ ...params })}`),
  get: (id: string) => apiRequest<RegisterDto>(`/api/registers/${id}`),
  create: (body: CreateRegisterRequest) =>
    apiRequest<RegisterDto>('/api/registers', { method: 'POST', body }),
  update: (id: string, body: UpdateRegisterRequest) =>
    apiRequest<RegisterDto>(`/api/registers/${id}`, { method: 'PUT', body }),
  deactivate: (id: string) =>
    apiRequest<void>(`/api/registers/${id}`, { method: 'DELETE' }),
}

export const sessionsApi = {
  open: (body: OpenRegisterSessionRequest) =>
    apiRequest<RegisterSessionDto>('/api/register-sessions/open', { method: 'POST', body }),
  current: (params: { registerId?: string; branchId?: string }) =>
    apiRequest<RegisterSessionDto>(`/api/register-sessions/current${qs({ ...params })}`),
  close: (id: string, body: CloseRegisterSessionRequest) =>
    apiRequest<RegisterSessionDto>(`/api/register-sessions/${id}/close`, { method: 'POST', body }),
}

export const posCatalogApi = {
  search: (params: PosCatalogParams) =>
    apiRequest<PagedResult<PosCatalogItemDto>>(`/api/pos/catalog${qs({ ...params })}`),
  barcode: (code: string, branchId: string) =>
    apiRequest<PosCatalogItemDto>(
      `/api/pos/catalog/barcode/${encodeURIComponent(code)}${qs({ branchId })}`,
    ),
}

export const checkoutApi = {
  checkout: (body: CheckoutRequest) =>
    apiRequest<SaleResultDto>('/api/pos/checkout', { method: 'POST', body }),
}

export const salesApi = {
  list: (params: SaleListParams) =>
    apiRequest<PagedResult<SaleSummaryDto>>(`/api/sales${qs({ ...params })}`),
  get: (id: string) => apiRequest<SaleDetailDto>(`/api/sales/${id}`),
  receipt: (id: string) => apiRequest<ReceiptDto>(`/api/sales/${id}/receipt`),
  listReturns: (id: string) => apiRequest<SaleReturnDto[]>(`/api/sales/${id}/returns`),
  createReturn: (id: string, body: CreateReturnRequest) =>
    apiRequest<SaleReturnDto>(`/api/sales/${id}/returns`, { method: 'POST', body }),
}

export const taxSettingsApi = {
  get: () => apiRequest<TaxSettingsDto>('/api/settings/tax'),
}
```

- [ ] **Step 3: Add capabilities to `src/lib/useCan.ts`**

Change the `Capability` type and `CAPABILITY_ROLES` map:

```ts
type Capability =
  | 'catalog:write'
  | 'inventory:write'
  | 'costs:view'
  | 'register:manage'
  | 'pos:operate'
  | 'sales:view'
  | 'refund:manage'

const CAPABILITY_ROLES: Record<Capability, ReadonlySet<UserRole>> = {
  'catalog:write': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'inventory:write': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'InventoryStaff']),
  'costs:view': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'InventoryStaff']),
  // Mirrors src/Negosio.Api/Authorization/AuthorizationPolicies.cs
  'register:manage': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'pos:operate': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
  'sales:view': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
  'refund:manage': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
}
```

- [ ] **Step 4: Create `src/lib/pos.ts`**

```ts
import type { PaymentMethod, SaleStatus } from '../api/types'

export const PAYMENT_METHOD_LABELS: Record<PaymentMethod, string> = {
  Cash: 'Cash',
  Card: 'Card',
  GCash: 'GCash',
  Maya: 'Maya',
  BankTransfer: 'Bank transfer',
  Other: 'Other',
}

/** Methods offered in the POS payment modal, in display order. */
export const POS_PAYMENT_METHODS: PaymentMethod[] = ['Cash', 'Card', 'GCash', 'Maya', 'BankTransfer']

export const SALE_STATUS_LABELS: Record<SaleStatus, string> = {
  Completed: 'Completed',
  Voided: 'Voided',
  Refunded: 'Refunded',
  PartiallyRefunded: 'Partially refunded',
}

export function saleStatusTone(s: SaleStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (s) {
    case 'Completed':
      return 'success'
    case 'PartiallyRefunded':
      return 'warning'
    case 'Refunded':
      return 'danger'
    case 'Voided':
      return 'neutral'
  }
}

/**
 * Quick-cash suggestions for a cash payment: the exact amount, then the next round
 * PHP notes above it (50 / 100 / 500 / 1000 boundaries), deduped, max 4.
 */
export function suggestCashButtons(total: number): number[] {
  if (!(total > 0)) return []
  const out = new Set<number>()
  out.add(Math.ceil(total * 100) / 100)
  for (const step of [50, 100, 500, 1000]) {
    out.add(Math.ceil(total / step) * step)
  }
  return [...out].sort((a, b) => a - b).slice(0, 4)
}
```

- [ ] **Step 5: Add the nav group in `src/lib/nav.ts`**

Add `Calculator` to the lucide import. Insert a new group **before** the trailing disabled group, and remove the now-real `POS`/`Sales` placeholders from that trailing group (leave `Reports`, `Staff`, `Settings`):

```ts
  {
    label: 'Point of sale',
    items: [
      { label: 'POS', icon: ShoppingCart, to: '/pos', enabled: true },
      { label: 'Registers', icon: Calculator, to: '/registers', enabled: true },
      { label: 'Sales', icon: ClipboardList, to: '/sales', enabled: true },
    ],
  },
  {
    items: [
      { label: 'Reports', icon: BarChart3, enabled: false },
      { label: 'Staff', icon: Users, enabled: false },
      { label: 'Settings', icon: Settings, enabled: false },
    ],
  },
```

- [ ] **Step 6: Wire routes in `src/App.tsx`**

Add to the `protectedRoutes` array: `/registers`, `/sales`, `/sales/:id`, `/sales/:id/receipt`. Add `/pos` and `/pos/complete/:saleId` as **separate** `<Route>` elements wrapped only in `<ProtectedRoute>` (they render their own shell, not `DashboardLayout`). Use `React.lazy` only if the existing file doesn't — it doesn't, so import directly. Placeholder page components can be a one-line `export default function X() { return null }` in this task; later tasks fill them. Create stub files: `src/pages/RegistersPage.tsx`, `src/pages/SalesPage.tsx`, `src/pages/SaleDetailPage.tsx`, `src/pages/ReceiptPage.tsx`, `src/pages/PosPage.tsx`, `src/pages/PosCompletePage.tsx`, each `export default function Name() { return null }`.

```tsx
// ...existing imports
import PosPage from './pages/PosPage'
import PosCompletePage from './pages/PosCompletePage'
import RegistersPage from './pages/RegistersPage'
import SalesPage from './pages/SalesPage'
import SaleDetailPage from './pages/SaleDetailPage'
import ReceiptPage from './pages/ReceiptPage'

const protectedRoutes: Array<{ path: string; element: ReactNode }> = [
  // ...existing
  { path: '/registers', element: <RegistersPage /> },
  { path: '/sales', element: <SalesPage /> },
  { path: '/sales/:id', element: <SaleDetailPage /> },
  { path: '/sales/:id/receipt', element: <ReceiptPage /> },
]
```

In the `<Routes>` body, alongside the `.map(...)`:

```tsx
<Route path="/pos" element={<ProtectedRoute><PosPage /></ProtectedRoute>} />
<Route path="/pos/complete/:saleId" element={<ProtectedRoute><PosCompletePage /></ProtectedRoute>} />
```

- [ ] **Step 7: Verify**

Run: `cd web/negosio-web && npm run lint && npm run build`
Expected: both pass. Sidebar shows a "Point of sale" group with POS / Registers / Sales enabled; navigating to each renders a blank page (stubs).

- [ ] **Step 8: Commit**

```bash
git add web/negosio-web/src/api/types.ts web/negosio-web/src/api/pos.ts web/negosio-web/src/lib/useCan.ts web/negosio-web/src/lib/pos.ts web/negosio-web/src/lib/nav.ts web/negosio-web/src/App.tsx web/negosio-web/src/pages/
git commit -m "feat(pos): types, api module, RBAC caps, nav group and routes"
```

---

## Task 2: `saleMath.ts` (preview-only) + tax settings query

**Files:**
- Create: `src/lib/saleMath.ts`
- Create: `src/hooks/useTaxSettings.ts`

**Interfaces:**
- Consumes: `taxSettingsApi.get` from `src/api/pos.ts`; `DiscountType`, `TaxSettingsDto` from types.
- Produces:
  - `roundMoney(n: number): number` — half-up at 2dp
  - `calcLine(input: { unitPrice: number; quantity: number; discount: { type: DiscountType; value: number }; taxRatePercent: number; pricesIncludeTax: boolean }): { gross: number; discount: number; tax: number; net: number }`
  - `calcTotals(lines: LineForMath[], tax: TaxSettingsDto): { subtotal: number; discountTotal: number; taxTotal: number; grandTotal: number }` where `LineForMath = { unitPrice, quantity, discount }`
  - `useTaxSettings(): { data?: TaxSettingsDto; isPending: boolean }` — TanStack Query, `queryKey: ['settings','tax']`, `staleTime: 5 * 60_000`

- [ ] **Step 1: Create `src/lib/saleMath.ts`**

Port `SaleLineCalculator.Calculate` (spec §2 / `src/Negosio.Application/Pos/SaleLineCalculator.cs`) **exactly** — half-up rounding, per-line then summed, tax exclusive by default, informational when `pricesIncludeTax`. Header comment must state this is a preview only.

```ts
import type { DiscountType, TaxSettingsDto } from '../api/types'

/**
 * PREVIEW ONLY. Mirrors src/Negosio.Application/Pos/SaleLineCalculator.cs so the cart can show
 * an estimated total before checkout. The backend recomputes every amount on POST /api/pos/checkout
 * and its SaleResultDto is authoritative — never reconcile against this, just show server values.
 */
export function roundMoney(n: number): number {
  // half-up at 2dp
  return Math.sign(n) * Math.round(Math.abs(n) * 100 + 1e-9) / 100
}

export interface LineForMath {
  unitPrice: number
  quantity: number
  discount: { type: DiscountType; value: number }
}

export function calcLine(input: LineForMath & { taxRatePercent: number; pricesIncludeTax: boolean }) {
  const { unitPrice, quantity, discount, taxRatePercent, pricesIncludeTax } = input
  const gross = roundMoney(unitPrice * quantity)

  let discountAmount = 0
  if (discount.type === 'Percentage') {
    const v = Math.min(Math.max(discount.value, 0), 100)
    discountAmount = roundMoney((gross * v) / 100)
  } else if (discount.type === 'FixedAmount') {
    discountAmount = Math.min(roundMoney(Math.max(discount.value, 0)), gross)
  }

  const taxable = gross - discountAmount
  let tax = 0
  const net = taxable
  if (taxRatePercent > 0) {
    if (pricesIncludeTax) {
      const netOfTax = roundMoney(taxable / (1 + taxRatePercent / 100))
      tax = taxable - netOfTax
    } else {
      tax = roundMoney((taxable * taxRatePercent) / 100)
    }
  }
  return { gross, discount: discountAmount, tax, net }
}

export function calcTotals(lines: LineForMath[], tax: TaxSettingsDto) {
  let subtotal = 0
  let discountTotal = 0
  let taxTotal = 0
  for (const l of lines) {
    const r = calcLine({ ...l, taxRatePercent: tax.taxRatePercent, pricesIncludeTax: tax.pricesIncludeTax })
    subtotal += r.gross
    discountTotal += r.discount
    taxTotal += r.tax
  }
  subtotal = roundMoney(subtotal)
  discountTotal = roundMoney(discountTotal)
  taxTotal = roundMoney(taxTotal)
  const grandTotal = tax.pricesIncludeTax
    ? roundMoney(subtotal - discountTotal)
    : roundMoney(subtotal - discountTotal + taxTotal)
  return { subtotal, discountTotal, taxTotal, grandTotal }
}
```

- [ ] **Step 2: Create `src/hooks/useTaxSettings.ts`**

```ts
import { useQuery } from '@tanstack/react-query'
import { taxSettingsApi } from '../api/pos'

export function useTaxSettings() {
  return useQuery({
    queryKey: ['settings', 'tax'],
    queryFn: taxSettingsApi.get,
    staleTime: 5 * 60_000,
  })
}
```

- [ ] **Step 3: Sanity-check the math against a known case**

Add a temporary scratch check (do NOT commit it) or reason it through: unitPrice 100, qty 2, 12% exclusive tax, no discount → gross 200, tax 24, grandTotal 224. `Percentage` 10% → discount 20, taxable 180, tax 21.6, grandTotal 201.6. Confirm `calcTotals` returns these.

- [ ] **Step 4: Verify**

Run: `cd web/negosio-web && npm run lint && npm run build`
Expected: both pass.

- [ ] **Step 5: Commit**

```bash
git add web/negosio-web/src/lib/saleMath.ts web/negosio-web/src/hooks/useTaxSettings.ts
git commit -m "feat(pos): preview-only sale math and tax-settings query"
```

---

## Task 3: Session modals (`OpenSessionModal`, `CloseSessionModal`)

**Files:**
- Create: `src/components/pos/OpenSessionModal.tsx`
- Create: `src/components/pos/CloseSessionModal.tsx`

**Interfaces:**
- Consumes: `sessionsApi` from `src/api/pos.ts`; `Modal`, `Button`, `TextField`, `Callout`, `useToast` from `src/components/ui`; `ApiError` from `src/api/client`; `formatMoney` from `src/lib/format`; `RegisterSessionDto` from types.
- Produces:
  - `OpenSessionModal({ open, onClose, registerId, registerName, onOpened }: { open: boolean; onClose: () => void; registerId: string; registerName: string; onOpened: (s: RegisterSessionDto) => void })`
  - `CloseSessionModal({ open, onClose, session, onClosed }: { open: boolean; onClose: () => void; session: RegisterSessionDto | null; onClosed: (s: RegisterSessionDto) => void })`

- [ ] **Step 1: Create `OpenSessionModal.tsx`**

Single numeric field "Opening cash" (`step="0.01"`, `min={0}`). Submit → `sessionsApi.open({ registerId, openingCash: Number(value) })` via `useMutation`. On `409` with code `REGISTER_SESSION_ALREADY_OPEN` → show `Callout tone="warning"` "This register already has an open session." and keep the modal open (parent will refetch). On other error → inline error / toast. On success → `onOpened(session)` then `onClose()`. Follow the structure of `src/components/inventory/AdjustStockModal.tsx` (reset local state in a `useEffect` on `open`; disable buttons while `mutation.isPending`).

Modal body header line: "Register: {registerName}".

- [ ] **Step 2: Create `CloseSessionModal.tsx`**

Two phases in one modal:
1. **Count phase** — field "Counted cash in drawer" (`step="0.01"`, `min={0}`) + Close button. Submit → `sessionsApi.close(session.id, { closingCash })`.
2. **Reconciliation phase** — after success, replace the form with a read-only `<dl>` (reuse the `AdjustStockModal` grey `<dl>` styling):
   - Opening cash — `formatMoney(session.openingCash)`
   - Expected in drawer — `formatMoney(result.expectedCash ?? 0)`
   - Counted — `formatMoney(result.closingCash ?? 0)`
   - **Difference** — `formatMoney(result.cashDifference ?? 0)`, tone success when `>= 0` ("over"), danger when `< 0` ("short"); append the word.
   Footer becomes a single **Done** button → `onClosed(result)` + `onClose()`.

Guard `if (!session) return null`.

- [ ] **Step 3: Verify**

Run: `cd web/negosio-web && npm run lint && npm run build`
Expected: both pass. (Interactive check happens in Task 4.)

- [ ] **Step 4: Commit**

```bash
git add web/negosio-web/src/components/pos/OpenSessionModal.tsx web/negosio-web/src/components/pos/CloseSessionModal.tsx
git commit -m "feat(pos): open/close register session modals"
```

---

## Task 4: Registers page

**Files:**
- Rewrite: `src/pages/RegistersPage.tsx`
- Create: `src/components/registers/RegisterFormModal.tsx`
- Create: `src/components/registers/RegisterSessionCell.tsx`

**Interfaces:**
- Consumes: `registersApi`, `sessionsApi` from `src/api/pos`; `branchesApi` from `src/api/inventory`; `usePagedQuery`, `useCan`; `Table`, `Pagination`, `SearchInput`? (registers list has no server search — skip SearchInput), `Button`, `Modal`, `ConfirmDialog`, `Select`, `TextField`, `Badge`, `EmptyState`, `ErrorState`, `SkeletonText`, `useToast`; `OpenSessionModal`, `CloseSessionModal` from Task 3; `formatMoney` from `src/lib/format`.
- Produces:
  - `RegisterFormModal({ open, onClose, register, branches }: { open: boolean; onClose: () => void; register: RegisterDto | null; branches: BranchDto[] })` — `register` null = create mode.
  - `RegisterSessionCell({ register }: { register: RegisterDto })` — renders live session state + open/close buttons.

- [ ] **Step 1: `RegisterFormModal.tsx`**

Fields: `name` (`TextField`), `code` (`TextField`), branch `Select` (hidden + auto-set when `branches.length === 1`; required otherwise), and on **edit** an "Active" checkbox. `useMutation` → `registersApi.create({ branchId, name, code })` or `registersApi.update(id, { name, code, isActive })`. On `409` code `REGISTER_ALREADY_EXISTS` → inline error on `code`: "A register with this code already exists in this branch." On success → `qc.invalidateQueries({ queryKey: ['registers'] })`, toast, `onClose()`. Reset state in `useEffect` on `open`.

- [ ] **Step 2: `RegisterSessionCell.tsx`**

```tsx
const sessionQuery = useQuery({
  queryKey: ['session', 'current', register.id],
  queryFn: () => sessionsApi.current({ registerId: register.id }),
  retry: false, // a 404 is the normal "no open session" state
})
```

- While `isPending` → `<SkeletonText className="w-24" />`.
- On error that is `ApiError` 404 → "No open session" + (if `useCan('pos:operate')`) an **Open session** `Button` (size sm) launching `OpenSessionModal` for this register. On `onOpened` → `sessionQuery.refetch()`.
- On success → `Open · {formatMoney(data.openingCash)} · since {new Date(data.openedAtUtc).toLocaleTimeString()}` + a **Close** `Button variant="ghost"` launching `CloseSessionModal`. On `onClosed` → `refetch()`.
- Any other error → small "—" with a retry affordance is optional; a plain "Unavailable" text is fine.

- [ ] **Step 3: `RegistersPage.tsx`**

`DashboardLayout title="Registers"`. Header: `<h1>Registers</h1>` + (gated `register:manage`) **New register** button. Filters: an `isActive` `Select` (All / Active / Inactive) via `usePagedQuery` (`DEFAULT_FILTERS = { status: undefined }` module-level const; map `'active'`→`isActive:true`, `'inactive'`→`false`). Branch filter only when `branches.length > 1`.

Query:
```tsx
const query = useQuery({
  queryKey: ['registers', { page: q.page, status: q.filters.status, branchId: q.filters.branchId }],
  queryFn: () => registersApi.list({
    page: q.page, pageSize: q.pageSize,
    isActive: q.filters.status === 'active' ? true : q.filters.status === 'inactive' ? false : undefined,
    branchId: q.filters.branchId,
  }),
})
```

Table columns: Name / Code / Branch (only when `branches.length > 1`) / Status (`Badge` success "Active" / neutral "Inactive") / Session (`<RegisterSessionCell register={r} />`) / Actions (gated: **Edit** ghost button → `RegisterFormModal`; **Deactivate**/**Reactivate** → `ConfirmDialog`; deactivate confirm body warns "This register has an open session — closing it is recommended first." when its `['session','current',id]` cache entry is a success — read via `qc.getQueryData`). Reactivate calls `registersApi.update(id, { name, code, isActive: true })`.

`EmptyState` (icon `Calculator`) when zero: "No registers yet" / "Create a register to start taking sales." + gated New button.

- [ ] **Step 4: Verify — build + interactive smoke**

Run: `cd web/negosio-web && npm run lint && npm run build` → pass.

Interactive (real API): start API (`ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Negosio.Api --launch-profile http` — kill any stale `Negosio.Api` first) + `npm run dev`. CDP-drive login as `invui@example.com` / `SecurePassword123!`, go to `/registers`:
- create a register "Front Counter" / "FC1" → appears in the list, Status Active, Session "No open session".
- click **Open session**, opening cash 1000 → cell shows "Open · ₱1,000.00 · since …".
- **Close** → count 1000 → reconciliation shows Expected ₱1,000.00, Difference ₱0.00 over.
- Deactivate → row flips to Inactive; Reactivate restores.
Capture a screenshot to the session scratchpad.

- [ ] **Step 5: Commit**

```bash
git add web/negosio-web/src/pages/RegistersPage.tsx web/negosio-web/src/components/registers/
git commit -m "feat(pos): registers page with live session state"
```

---

## Task 5: POS storage helpers + cart hook + barcode hook

**Files:**
- Create: `src/lib/posStorage.ts`
- Create: `src/hooks/usePosCart.ts`
- Create: `src/hooks/useBarcodeScanner.ts`

**Interfaces:**
- Consumes: `PosCatalogItemDto`, `DiscountType` from types.
- Produces:
  - `posStorage.cartKey(ctx: TerminalCtx): string`, `posStorage.readCart(ctx): CartLine[] | null`, `posStorage.writeCart(ctx, lines: CartLine[]): void`, `posStorage.clearCart(ctx): void`, `posStorage.pruneOtherCarts(ctx): void`, `posStorage.readRegister(ctx: { tenantId; branchId }): string | null`, `posStorage.writeRegister(ctx, registerId: string): void`
  - `TerminalCtx = { tenantId: string; branchId: string; registerSessionId: string }`
  - `CartLine = { variantId: string; productId: string; name: string; variantName: string | null; sku: string | null; unitPrice: number; quantity: number; discount: { type: DiscountType; value: number } }`
  - `usePosCart(ctx: TerminalCtx | null): { lines: CartLine[]; addItem(item: PosCatalogItemDto): void; setQty(variantId: string, qty: number): void; removeLine(variantId: string): void; setLineDiscount(variantId: string, d: { type: DiscountType; value: number }): void; clear(): void; isEmpty: boolean }`
  - `useBarcodeScanner(opts: { enabled: boolean; onScan: (code: string) => void }): void`

- [ ] **Step 1: `src/lib/posStorage.ts`**

```ts
import type { DiscountType } from '../api/types'

export interface TerminalCtx {
  tenantId: string
  branchId: string
  registerSessionId: string
}

export interface CartLine {
  variantId: string
  productId: string
  name: string
  variantName: string | null
  sku: string | null
  unitPrice: number
  quantity: number
  discount: { type: DiscountType; value: number }
}

const CART_PREFIX = 'negosio.pos.cart.v1.'
const REGISTER_PREFIX = 'negosio.pos.register.v1.'

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}
function safeSet(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    /* private mode / quota — cart durability is best-effort */
  }
}
function safeRemove(key: string): void {
  try {
    localStorage.removeItem(key)
  } catch {
    /* ignore */
  }
}

export const posStorage = {
  cartKey: (c: TerminalCtx) => `${CART_PREFIX}${c.tenantId}/${c.branchId}/${c.registerSessionId}`,

  readCart(c: TerminalCtx): CartLine[] | null {
    const raw = safeGet(posStorage.cartKey(c))
    if (!raw) return null
    try {
      const parsed = JSON.parse(raw)
      return Array.isArray(parsed) ? (parsed as CartLine[]) : null
    } catch {
      return null
    }
  },
  writeCart: (c: TerminalCtx, lines: CartLine[]) =>
    safeSet(posStorage.cartKey(c), JSON.stringify(lines)),
  clearCart: (c: TerminalCtx) => safeRemove(posStorage.cartKey(c)),

  /** Drop cart keys for the same tenant/branch that belong to other (stale) sessions. */
  pruneOtherCarts(c: TerminalCtx) {
    const keep = posStorage.cartKey(c)
    const stalePrefix = `${CART_PREFIX}${c.tenantId}/${c.branchId}/`
    try {
      for (let i = localStorage.length - 1; i >= 0; i--) {
        const k = localStorage.key(i)
        if (k && k.startsWith(stalePrefix) && k !== keep) localStorage.removeItem(k)
      }
    } catch {
      /* ignore */
    }
  },

  readRegister: (c: { tenantId: string; branchId: string }) =>
    safeGet(`${REGISTER_PREFIX}${c.tenantId}/${c.branchId}`),
  writeRegister: (c: { tenantId: string; branchId: string }, registerId: string) =>
    safeSet(`${REGISTER_PREFIX}${c.tenantId}/${c.branchId}`, registerId),
}
```

- [ ] **Step 2: `src/hooks/usePosCart.ts`**

`useReducer` over `CartLine[]`. Actions: `add` (find by `variantId`; if present `quantity += 1` else push with `quantity: 1`, `discount: { type: 'None', value: 0 }`), `setQty` (drop line when `qty <= 0`, else set; round to 3dp), `remove`, `setDiscount`, `clear`, `hydrate`. On mount (when `ctx` becomes non-null) dispatch `hydrate` from `posStorage.readCart(ctx)` and call `posStorage.pruneOtherCarts(ctx)`. In a `useEffect` on `[lines, ctx]` write through with `posStorage.writeCart`. `clear()` also calls `posStorage.clearCart(ctx)`. Guard every branch for `ctx === null` (no-op).

- [ ] **Step 3: `src/hooks/useBarcodeScanner.ts`**

```ts
import { useEffect, useRef } from 'react'

const RESET_MS = 50
const MIN_LEN = 3

export function useBarcodeScanner({ enabled, onScan }: { enabled: boolean; onScan: (code: string) => void }) {
  const buf = useRef('')
  const last = useRef(0)
  const onScanRef = useRef(onScan)
  onScanRef.current = onScan

  useEffect(() => {
    if (!enabled) return
    function handler(e: KeyboardEvent) {
      const now = Date.now()
      if (now - last.current > RESET_MS) buf.current = ''
      last.current = now

      const target = e.target as HTMLElement | null
      const inModalField =
        !!target &&
        (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') &&
        !!target.closest('[role="dialog"]')
      if (inModalField) return

      if (e.key === 'Enter') {
        const code = buf.current
        buf.current = ''
        if (code.length >= MIN_LEN) {
          e.preventDefault()
          onScanRef.current(code)
        }
        return
      }
      if (e.key.length === 1) buf.current += e.key
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [enabled])
}
```

- [ ] **Step 4: Verify**

Run: `cd web/negosio-web && npm run lint && npm run build` → pass.

- [ ] **Step 5: Commit**

```bash
git add web/negosio-web/src/lib/posStorage.ts web/negosio-web/src/hooks/usePosCart.ts web/negosio-web/src/hooks/useBarcodeScanner.ts
git commit -m "feat(pos): scoped cart storage, cart reducer, barcode-scanner hook"
```

---

## Task 6: POS shell, register picker, session gate

**Files:**
- Rewrite: `src/pages/PosPage.tsx`
- Create: `src/components/pos/PosShell.tsx`
- Create: `src/components/pos/RegisterPicker.tsx`
- Create: `src/components/pos/PosSessionGate.tsx`

**Interfaces:**
- Consumes: `registersApi`, `sessionsApi` from `src/api/pos`; `branchesApi` from `src/api/inventory`; `useAuth`; `useCan`; `posStorage`; `OpenSessionModal` (Task 3); `Button`, `Select`, `EmptyState`, `ErrorState`, `Callout`, `SkeletonText`; `ApiError`.
- Produces:
  - `PosShell({ session, register, onCloseSession, children }: { session: RegisterSessionDto; register: { id: string; name: string }; onCloseSession: () => void; children: ReactNode })` — renders the top bar + children in a full-screen flex column.
  - `RegisterPicker({ registers, onPick }: { registers: RegisterDto[]; onPick: (id: string) => void })`
  - `PosSessionGate({ register, onOpened, onSwitchRegister }: { register: RegisterDto; onOpened: () => void; onSwitchRegister: () => void })`
  - `PosPage` default export — the state machine below; renders `<PosTerminal>` (Task 7) once a session is resolved. Until Task 7 lands, render a placeholder `<div>Terminal</div>` where `<PosTerminal>` will go.

- [ ] **Step 1: `PosShell.tsx`**

Full-viewport: `<div className="flex h-screen flex-col bg-background">`. Top bar (`h-14 shrink-0 border-b ... flex items-center justify-between px-4`):
- Left: store name (`user.tenantName`), then `· {register.name}`.
- Center/right: `Open · ₱{openingCash} · {cashier firstName}` small muted; an overflow `Button variant="ghost"` "Close session" → `onCloseSession`; **Exit** link (`<Link to="/dashboard">`).
Body: `<div className="min-h-0 flex-1">{children}</div>`.

- [ ] **Step 2: `RegisterPicker.tsx`**

Centered card. Title "Choose a register". A `Select` of `registers` (active only, passed in) + **Continue** button → `onPick(selectedId)`. If `registers.length === 0`, an `EmptyState` (icon `Calculator`) "No active registers" with a `<Link to="/registers">` "Manage registers" (only meaningful for `register:manage`, but the link is harmless for all).

- [ ] **Step 3: `PosSessionGate.tsx`**

Centered card. "Register {register.name} has no open session." Inline opening-cash number field + **Open register** button → `sessionsApi.open({ registerId: register.id, openingCash })` via `useMutation`; on `409 REGISTER_SESSION_ALREADY_OPEN` → `Callout` + call `onOpened()` (parent refetches and finds it). On success → `onOpened()`. A secondary link "Choose a different register" → `onSwitchRegister()`. (You may reuse `OpenSessionModal`'s mutation logic, but here it's inline, not a modal.)

- [ ] **Step 4: `PosPage.tsx` — state machine**

```tsx
export default function PosPage() {
  const { user } = useAuth()
  const canOperate = useCan('pos:operate')
  const branchesQuery = useQuery({ queryKey: ['branches'], queryFn: branchesApi.list })
  const branch = branchesQuery.data?.[0]

  const registersQuery = useQuery({
    queryKey: ['registers', 'active-for-pos'],
    queryFn: () => registersApi.list({ isActive: true, pageSize: 100 }),
    enabled: !!branch,
  })
  const activeRegisters = registersQuery.data?.items ?? []

  const [registerId, setRegisterId] = useState<string | null>(null)

  // Resolve the register: persisted choice (validated) → sole active register → picker.
  useEffect(() => {
    if (!branch || registersQuery.isPending) return
    const persisted = posStorage.readRegister({ tenantId: user!.tenantId, branchId: branch.id })
    const valid = persisted && activeRegisters.some((r) => r.id === persisted) ? persisted : null
    if (valid) setRegisterId(valid)
    else if (activeRegisters.length === 1) setRegisterId(activeRegisters[0].id)
    // else leave null → RegisterPicker
  }, [branch, registersQuery.isPending]) // eslint-disable-line react-hooks/exhaustive-deps

  const sessionQuery = useQuery({
    queryKey: ['session', 'current', registerId],
    queryFn: () => sessionsApi.current({ registerId: registerId! }),
    enabled: !!registerId,
    retry: false,
  })

  if (!canOperate) return <PosDenied />        // small full-screen "You don't have POS access"
  if (branchesQuery.isPending || registersQuery.isPending) return <PosLoading />
  if (!registerId) {
    return <PosCentered><RegisterPicker registers={activeRegisters} onPick={(id) => {
      posStorage.writeRegister({ tenantId: user!.tenantId, branchId: branch!.id }, id)
      setRegisterId(id)
    }} /></PosCentered>
  }
  const register = activeRegisters.find((r) => r.id === registerId)!
  if (sessionQuery.isError) {
    const err = sessionQuery.error
    if (err instanceof ApiError && err.status === 404) {
      return <PosCentered><PosSessionGate register={register}
        onOpened={() => sessionQuery.refetch()}
        onSwitchRegister={() => setRegisterId(null)} /></PosCentered>
    }
    return <PosCentered><ErrorState message={(err as Error).message} onRetry={() => sessionQuery.refetch()} /></PosCentered>
  }
  if (sessionQuery.isPending || !sessionQuery.data) return <PosLoading />

  const session = sessionQuery.data
  return (
    <PosShell
      session={session}
      register={register}
      onCloseSession={/* opens CloseSessionModal; on close → setRegisterId stays, sessionQuery.refetch() → 404 → gate */}
    >
      {/* Task 7: <PosTerminal ctx={{ tenantId: user.tenantId, branchId: branch.id, registerSessionId: session.id }} branchId={branch.id} /> */}
      <div>Terminal placeholder</div>
    </PosShell>
  )
}
```

Implement the small helper components `PosDenied`, `PosLoading`, `PosCentered` inline in the file. `onCloseSession` holds a `useState` boolean to mount `CloseSessionModal` (from Task 3); on `onClosed` → `sessionQuery.refetch()`.

- [ ] **Step 5: Verify — build + interactive smoke**

`npm run lint && npm run build` → pass.

Interactive: with the register + session created in Task 4, load `/pos`:
- one active register → auto-selected, no picker; session already open → shell renders with top bar "Inv UI Test · Front Counter", "Terminal placeholder" body.
- Close the session from the top bar → gate appears; reopen → back to shell.
- Deactivate the register in another tab / create a 2nd active register → reload `/pos` → `RegisterPicker` appears.

- [ ] **Step 6: Commit**

```bash
git add web/negosio-web/src/pages/PosPage.tsx web/negosio-web/src/components/pos/PosShell.tsx web/negosio-web/src/components/pos/RegisterPicker.tsx web/negosio-web/src/components/pos/PosSessionGate.tsx
git commit -m "feat(pos): full-screen shell, register picker, session gate"
```

---

## Task 7: POS terminal — search, product grid, cart, discounts

**Files:**
- Create: `src/components/pos/PosTerminal.tsx`
- Create: `src/components/pos/PosSearchBar.tsx`
- Create: `src/components/pos/PosProductGrid.tsx`
- Create: `src/components/pos/CartPanel.tsx`
- Create: `src/components/pos/LineDiscountPopover.tsx`
- Modify: `src/pages/PosPage.tsx` (swap the placeholder for `<PosTerminal>`)

**Interfaces:**
- Consumes: `posCatalogApi` from `src/api/pos`; `usePosCart`, `useBarcodeScanner` (Task 5); `useTaxSettings`, `calcLine`, `calcTotals` (Task 2); `useDebouncedValue` from `src/hooks`; `SearchInput`, `Pagination`, `Button`, `Select`, `EmptyState`, `SkeletonText`, `Callout`, `useToast`; `formatMoney`, `formatQty`; `ApiError`.
- Produces:
  - `PosTerminal({ ctx, branchId, onCheckoutRequested }: { ctx: TerminalCtx; branchId: string; onCheckoutRequested: (payload: CheckoutAttempt) => void })` — where `CheckoutAttempt = { lines: CartLine[]; previewTotal: number }`. **Task 7 stops at the "Charge" button calling `onCheckoutRequested`.** The `PaymentModal` + checkout wiring is Task 8; for now `PosPage` passes a no-op that `console.warn`s.
  - Exposes `cart` API up to `PosPage`? No — `PosTerminal` owns the cart internally. Task 8 lifts the `clientRequestId` + payment modal into `PosTerminal` too (it already owns cart state, per spec §9). Design `PosTerminal` so Task 8 adds state without moving the cart.

- [ ] **Step 1: `PosSearchBar.tsx`**

Controlled `SearchInput` wrapper: `{ value, onChange, onEnter }`. `onKeyDown` Enter → `onEnter(value)` (used for barcode-first lookup). `autoFocus`.

- [ ] **Step 2: `PosProductGrid.tsx`**

Props `{ items: PosCatalogItemDto[]; onAdd: (item) => void }`. Grid `grid grid-cols-2 md:grid-cols-3 xl:grid-cols-4 gap-2`. Each item = a `<button>` card: product name (bold, clamp 2 lines), variant name (muted, when non-null), `formatMoney(sellingPrice)`, and a stock chip — `formatQty(quantityAvailable)` in stock when `trackInventory`, hidden otherwise. `disabled={!item.isAvailable}` with reduced opacity + "Out of stock" label. Click → `onAdd(item)`.

- [ ] **Step 3: `LineDiscountPopover.tsx`**

Props `{ discount: { type, value }; onChange: (d) => void; onClose: () => void }`. A small absolutely-positioned panel (not a full `Modal`): `Select` type (None / Percentage / FixedAmount with friendly labels "No discount" / "% off" / "₱ off") + a number field for `value` (hidden when type None). "Apply" → `onChange` + `onClose`. Close on outside click / Escape.

- [ ] **Step 4: `CartPanel.tsx`**

Props: `{ lines: CartLine[]; totals: { subtotal; discountTotal; taxTotal; grandTotal }; taxPending: boolean; onSetQty; onRemove; onSetDiscount; onCharge; chargeDisabled: boolean; notice?: ReactNode }`.
- Scrollable line list; each `CartLineRow`: name + variant, unit price, qty stepper (`−` / editable number / `+` — `onSetQty`), a discount button showing the current discount (`10% off` / `₱5 off` / `Discount`) that toggles `LineDiscountPopover`, line net (`calcLine` result net; when `taxPending` show gross), remove `✕`.
- `CartSummary` footer: Subtotal / Discount (when `> 0`) / Tax (label "Tax" + a muted "(estimated)") / **Total** big. When `taxPending`, show a one-line "Calculating tax…" and still allow charging (server is authoritative).
- `notice` slot renders a `Callout` above the summary (checkout errors land here in Task 8).
- **Charge** button: `w-full`, `formatMoney(totals.grandTotal)`, `disabled={chargeDisabled || lines.length === 0}` → `onCharge()`.

- [ ] **Step 5: `PosTerminal.tsx`**

```tsx
export function PosTerminal({ ctx, branchId, onCheckoutRequested }: PosTerminalProps) {
  const cart = usePosCart(ctx)
  const tax = useTaxSettings()
  const { toast } = useToast()
  const [term, setTerm] = useState('')
  const [page, setPage] = useState(1)
  const debounced = useDebouncedValue(term, 300)

  const catalog = useQuery({
    queryKey: ['pos-catalog', { branchId, search: debounced, page }],
    queryFn: () => posCatalogApi.search({ branchId, search: debounced || undefined, page, pageSize: 24 }),
  })

  async function lookupBarcode(code: string) {
    try {
      const item = await posCatalogApi.barcode(code, branchId)
      cart.addItem(item)
      toast('success', `Added ${item.productName}`)
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) toast('info', `No product for barcode ${code}`)
      else toast('error', e instanceof Error ? e.message : 'Lookup failed')
    }
  }

  useBarcodeScanner({ enabled: true, onScan: lookupBarcode })

  const totals = tax.data
    ? calcTotals(cart.lines.map((l) => ({ unitPrice: l.unitPrice, quantity: l.quantity, discount: l.discount })), tax.data)
    : { subtotal: 0, discountTotal: 0, taxTotal: 0, grandTotal: 0 }

  return (
    <div className="grid h-full min-h-0 grid-rows-[auto_1fr] lg:grid-cols-[62%_38%] lg:grid-rows-1">
      <section className="flex min-h-0 flex-col gap-3 border-r border-border p-4">
        <PosSearchBar value={term} onChange={setTerm} onEnter={(v) => v && lookupBarcode(v)} />
        {/* pending: skeleton grid; error: ErrorState; empty: EmptyState; else grid + Pagination */}
        <PosProductGrid items={catalog.data?.items ?? []} onAdd={cart.addItem} />
        <Pagination page={catalog.data?.page ?? 1} pageSize={24} totalCount={catalog.data?.totalCount ?? 0} totalPages={catalog.data?.totalPages ?? 1} onPageChange={setPage} />
      </section>
      <aside className="flex min-h-0 flex-col">
        <CartPanel
          lines={cart.lines}
          totals={totals}
          taxPending={tax.isPending}
          onSetQty={cart.setQty}
          onRemove={cart.removeLine}
          onSetDiscount={cart.setLineDiscount}
          onCharge={() => onCheckoutRequested({ lines: cart.lines, previewTotal: totals.grandTotal })}
          chargeDisabled={false}
        />
      </aside>
    </div>
  )
}
```

Reset `page` to 1 whenever `debounced` changes (effect).

- [ ] **Step 6: Wire into `PosPage.tsx`**

Replace the placeholder with `<PosTerminal ctx={{ tenantId: user.tenantId, branchId: branch.id, registerSessionId: session.id }} branchId={branch.id} onCheckoutRequested={(p) => console.warn('checkout', p)} />`.

- [ ] **Step 7: Verify — build + interactive smoke**

`npm run lint && npm run build` → pass.

Interactive (`invui` tenant has Widget A / Widget B with stock):
- `/pos` terminal renders; product grid shows Widget A/B with prices + stock chips.
- Click Widget A twice → cart line qty 2; `−`/`+` adjust; set a 10% line discount → line + summary update; Tax line shows "(estimated)".
- Type a known barcode into the search box + Enter → item added (or "No product for barcode" toast).
- Simulate a scanner: dispatch rapid `keydown`s ending in Enter via CDP → item added.
- Reload the page → cart persists (localStorage). Check the key is `negosio.pos.cart.v1.<tenant>/<branch>/<session>`.
- **Charge** logs the attempt (no-op).

- [ ] **Step 8: Commit**

```bash
git add web/negosio-web/src/components/pos/PosTerminal.tsx web/negosio-web/src/components/pos/PosSearchBar.tsx web/negosio-web/src/components/pos/PosProductGrid.tsx web/negosio-web/src/components/pos/CartPanel.tsx web/negosio-web/src/components/pos/LineDiscountPopover.tsx web/negosio-web/src/pages/PosPage.tsx
git commit -m "feat(pos): terminal — product search, grid, cart, line discounts, barcode"
```

---

## Task 8: Payment modal + checkout mutation + idempotency lifecycle

**Files:**
- Create: `src/components/pos/PaymentModal.tsx`
- Modify: `src/components/pos/PosTerminal.tsx` (own the checkout attempt + `clientRequestId` + payment modal)
- Modify: `src/pages/PosPage.tsx` (navigate to `/pos/complete/:saleId` on success)

**Interfaces:**
- Consumes: `checkoutApi` from `src/api/pos`; `POS_PAYMENT_METHODS`, `PAYMENT_METHOD_LABELS`, `suggestCashButtons` from `src/lib/pos`; `Modal`, `Button`, `TextField`, `Callout`; `formatMoney`; `ApiError`; `useMutation`, `useQueryClient`; `useNavigate`.
- Produces:
  - `PaymentModal({ open, onClose, amountDue, submitting, error, onConfirm }: { open: boolean; onClose: () => void; amountDue: number; submitting: boolean; error: string | null; onConfirm: (payment: CheckoutPaymentInput) => void })`
  - `PosTerminal` now: holds `clientRequestIdRef` (see lifecycle below), `status`, `checkoutError`, mounts `PaymentModal`, calls `checkoutApi.checkout`, and on success calls a prop `onCheckoutSuccess(result: SaleResultDto)`.

**`clientRequestId` lifecycle (spec §9 — implement exactly):**
- A `useRef<string | null>(null)` in `PosTerminal`. Helper `ensureId()` → if null, set `crypto.randomUUID()`; return it.
- Call `ensureId()` when the cart transitions **empty → non-empty** (effect on `cart.isEmpty`).
- **Do NOT** rotate the id when the `PaymentModal` opens/closes.
- Rotate (`ref.current = null`) only:
  1. Right after a **successful** checkout (before navigating away / clearing cart).
  2. On the **next cart mutation** (`addItem`/`setQty`/`removeLine`/`setLineDiscount`) that happens **after** a *definitive* rejection flag is set. Track `needsNewIdOnEdit: boolean` state — set `true` on `INSUFFICIENT_INVENTORY` / `INVALID_SALE_ITEM` / `INVALID_QUANTITY` / `INVALID_DISCOUNT`; when it's `true` and a cart mutation occurs, set `ref.current = null` and clear the flag. Wrap the `usePosCart` callbacks to intercept.
- Keep the id (no rotation) on `NETWORK_ERROR` and `CHECKOUT_CONCURRENCY_CONFLICT` (transient) — Retry reuses it.

- [ ] **Step 1: `PaymentModal.tsx`**

- Big `formatMoney(amountDue)` "Amount due".
- Segmented method buttons from `POS_PAYMENT_METHODS` (pattern: the direction toggle in `AdjustStockModal`).
- **Cash:** `TextField` "Cash received" (`step="0.01"`, `min={0}`, autofocus) + quick-cash `Button`s from `suggestCashButtons(amountDue)` (each sets the field). Live **Change** = `formatMoney(Math.max(0, received - amountDue))`. Confirm disabled unless `received >= amountDue`.
- **Non-cash:** optional `TextField` "Reference number". Confirm always enabled.
- `error` prop → `Callout tone="danger"` at top.
- Footer: Cancel (disabled while `submitting`) + **Confirm payment** (`loading={submitting}`).
- `onConfirm`:
  - Cash → `{ method: 'Cash', receivedAmount: Number(received) }`
  - else → `{ method, amount: amountDue, referenceNumber: ref || null }`

- [ ] **Step 2: `PosTerminal` checkout wiring**

```tsx
const [payOpen, setPayOpen] = useState(false)
const [status, setStatus] = useState<'idle' | 'submitting' | 'failed'>('idle')
const [checkoutError, setCheckoutError] = useState<string | null>(null)   // shown in CartPanel notice
const [payError, setPayError] = useState<string | null>(null)             // shown in PaymentModal
const [needsNewId, setNeedsNewId] = useState(false)
const idRef = useRef<string | null>(null)

const mutation = useMutation({
  mutationFn: (payment: CheckoutPaymentInput) => {
    const clientRequestId = idRef.current ?? (idRef.current = crypto.randomUUID())
    const body: CheckoutRequest = {
      branchId,
      registerSessionId: ctx.registerSessionId,
      clientRequestId,
      items: cart.lines.map((l) => ({
        productVariantId: l.variantId,
        quantity: l.quantity,
        discount: l.discount.type === 'None' ? null : l.discount,
      })),
      payments: [payment],
    }
    return checkoutApi.checkout(body)
  },
  onMutate: () => { setStatus('submitting'); setPayError(null); setCheckoutError(null) },
  onSuccess: (result) => {
    idRef.current = null                 // rotation rule 1
    setStatus('idle'); setPayOpen(false)
    cart.clear()
    qc.invalidateQueries({ queryKey: ['inventory'] })
    qc.invalidateQueries({ queryKey: ['sales'] })
    qc.invalidateQueries({ queryKey: ['dashboard'] })
    onCheckoutSuccess(result)
  },
  onError: (err) => {
    setStatus('failed')
    if (!(err instanceof ApiError)) { setPayError('Payment failed.'); return }
    switch (err.code) {
      case 'NETWORK_ERROR':
        setPayError("We couldn't confirm the sale. Check your last sale under Sales, then Retry — it won't charge twice.")
        break
      case 'CHECKOUT_CONCURRENCY_CONFLICT':
        setPayError('That didn\u2019t go through. Please retry.')
        break
      case 'INSUFFICIENT_INVENTORY':
        setPayOpen(false)
        setCheckoutError('Not enough stock for one or more items. Stock levels have been refreshed — adjust quantities and charge again.')
        setNeedsNewId(true)
        catalog.refetch()
        break
      case 'PAYMENT_INSUFFICIENT':
      case 'INVALID_PAYMENT':
        setPayError(err.message)
        break
      case 'INVALID_SALE_ITEM':
      case 'INVALID_QUANTITY':
      case 'INVALID_DISCOUNT':
        setPayOpen(false)
        setCheckoutError(err.message + ' Review the flagged item and try again.')
        setNeedsNewId(true)
        break
      case 'REGISTER_SESSION_NOT_FOUND':
      case 'REGISTER_SESSION_NOT_OPEN':
        onSessionLost()                 // PosPage drops to the gate; cart preserved
        break
      default:
        setPayError(err.message)
    }
  },
})
```

Wrap cart mutators so that when `needsNewId` is true, the first call sets `idRef.current = null` and `setNeedsNewId(false)` before delegating.

`CartPanel` gets `notice={checkoutError && <Callout tone="warning">{checkoutError}</Callout>}` and `chargeDisabled={status === 'submitting'}`. `onCharge` → `setPayOpen(true)` (does **not** touch `idRef`).

- [ ] **Step 3: `PosPage` success handling**

Pass `onCheckoutSuccess={(result) => navigate('/pos/complete/' + result.saleId, { state: { result } })}` and `onSessionLost={() => sessionQuery.refetch()}` into `PosTerminal`.

- [ ] **Step 4: Verify — build + interactive smoke**

`npm run lint && npm run build` → pass.

Interactive:
- Build a cart, **Charge**, PaymentModal → Cash, received > total → Change shows → **Confirm** → navigates to `/pos/complete/:saleId`; cart cleared; `localStorage` cart key gone.
- **Idempotency:** rebuild a cart; open PaymentModal, note nothing rotates; close + reopen → same attempt. Use CDP to throttle offline, Confirm → network error message, Retry online → single sale (verify only one sale in `/sales`).
- **INSUFFICIENT_INVENTORY:** set a variant's stock to 1 via the inventory UI, cart qty 5, Charge/Confirm → modal closes, cart callout, grid stock refreshes; lower qty to 1 → the id rotates on that edit; Confirm → succeeds.
- **Double-submit:** Confirm is disabled while submitting (observe the button).

- [ ] **Step 5: Commit**

```bash
git add web/negosio-web/src/components/pos/PaymentModal.tsx web/negosio-web/src/components/pos/PosTerminal.tsx web/negosio-web/src/pages/PosPage.tsx
git commit -m "feat(pos): payment modal, checkout, and clientRequestId idempotency lifecycle"
```

---

## Task 9: Sale-complete screen + receipt page (print)

**Files:**
- Rewrite: `src/pages/PosCompletePage.tsx`
- Rewrite: `src/pages/ReceiptPage.tsx`
- Create: `src/components/sales/StatusBadge.tsx`

**Interfaces:**
- Consumes: `salesApi` from `src/api/pos`; `SALE_STATUS_LABELS`, `saleStatusTone` from `src/lib/pos`; `Badge`, `Button`; `formatMoney`; `useParams`, `useLocation`, `Link`, `useNavigate`; `useQuery`.
- Produces:
  - `StatusBadge({ status }: { status: SaleStatus })` → `<Badge tone={saleStatusTone(status)}>{SALE_STATUS_LABELS[status]}</Badge>`

- [ ] **Step 1: `StatusBadge.tsx`** — trivial, per interface above.

- [ ] **Step 2: `PosCompletePage.tsx`**

Full-screen centered (own shell, not `DashboardLayout`). Read `location.state?.result as SaleResultDto | undefined`; if absent (hard refresh), `useQuery(['sales', saleId], () => salesApi.get(saleId))` and map `SaleDetailDto` → the same fields.
Show: big check icon; **Sale {saleNumber}**; `formatMoney(grandTotal)` total; `Amount paid` / `Change due` rows.
Buttons: **New sale** (`<Link to="/pos">`), **View sale** (`<Link to={'/sales/' + saleId}>`), **Print receipt** (`<Link to={'/sales/' + saleId + '/receipt?print=1'}>`).

- [ ] **Step 3: `ReceiptPage.tsx`**

`useQuery(['sales', id, 'receipt'], () => salesApi.receipt(id))`. Layout an 80 mm receipt inside a scoped wrapper:

```tsx
<div className="receipt-page">
  <style>{`
    .receipt-page { display:flex; flex-direction:column; align-items:center; background:#f3f4f6; min-height:100vh; padding:24px; }
    .receipt { width:80mm; background:#fff; color:#000; padding:6mm 4mm; font: 12px/1.4 ui-monospace, Menlo, Consolas, monospace; }
    .receipt h1 { font-size:14px; text-align:center; margin:0 0 4px; }
    .receipt .muted { color:#333; }
    .receipt table { width:100%; border-collapse:collapse; }
    .receipt .row { display:flex; justify-content:space-between; }
    .receipt hr { border:0; border-top:1px dashed #000; margin:6px 0; }
    .receipt-actions { margin-top:16px; display:flex; gap:8px; }
    @media print {
      .receipt-page { background:#fff; padding:0; display:block; }
      .receipt { width:auto; padding:0; }
      .receipt-actions, .app-chrome { display:none !important; }
      @page { margin:4mm; }
    }
  `}</style>
  {/* store name, branch, register, sale #, cashier, date; hr; lines (name/variant, qty × unit, net); hr;
      subtotal / discount (if >0) / tax / TOTAL; payments; change; status banner when Refunded/PartiallyRefunded;
      "Thank you" */}
  <div className="receipt-actions">
    <Button onClick={() => window.print()}>Print</Button>
    <Link to={-1 as unknown as string}>Back</Link>   {/* or navigate(-1) */}
  </div>
</div>
```

If `?print=1` (via `useSearchParams`) → `useEffect` that waits for the query to succeed then `requestAnimationFrame(() => window.print())` once.

This page renders **outside** `DashboardLayout` (it's in `protectedRoutes`, which currently wraps only in `ProtectedRoute` — good, no dashboard chrome). Confirm `protectedRoutes` elements are NOT wrapped in `DashboardLayout` by the router (they aren't — each page opts in itself). Since `ReceiptPage` must not show sidebar, simply don't use `DashboardLayout`.

- [ ] **Step 4: Verify — build + interactive smoke**

`npm run lint && npm run build` → pass.

Interactive: after a checkout, land on `/pos/complete/:id` → correct number / paid / change. **Print receipt** → receipt view; `window.print()` (in headless, assert the dialog call via CDP `Page.printToPDF` is not needed — just assert layout renders and no console errors). Hard-refresh `/pos/complete/:id` → still renders (falls back to `salesApi.get`).

- [ ] **Step 5: Commit**

```bash
git add web/negosio-web/src/pages/PosCompletePage.tsx web/negosio-web/src/pages/ReceiptPage.tsx web/negosio-web/src/components/sales/StatusBadge.tsx
git commit -m "feat(pos): sale-complete screen and printable receipt page"
```

---

## Task 10: Sales list

**Files:**
- Rewrite: `src/pages/SalesPage.tsx`

**Interfaces:**
- Consumes: `salesApi`; `branchesApi`; `usePagedQuery`; `Table`, `Pagination`, `SearchInput`, `Select`, `EmptyState`, `ErrorState`, `SkeletonText`; `StatusBadge` (Task 9); `formatMoney`; `SALE_STATUS_LABELS`; `Link`.
- Produces: nothing downstream.

- [ ] **Step 1: `SalesPage.tsx`**

`DashboardLayout title="Sales"`. `usePagedQuery` — `DEFAULT_FILTERS = { status: undefined, from: undefined, to: undefined }` (module-level const). Filters row: `SearchInput` (sale number), status `Select` (All + the four `SALE_STATUS_LABELS`), two date `<input type="date">` (from / to) → convert to `fromUtc = new Date(from + 'T00:00:00').toISOString()`, `toUtc = new Date(to + 'T23:59:59').toISOString()`.

```tsx
const query = useQuery({
  queryKey: ['sales', { page: q.page, search: q.search, status: q.filters.status, from: q.filters.from, to: q.filters.to }],
  queryFn: () => salesApi.list({
    page: q.page, pageSize: q.pageSize,
    search: q.search || undefined,
    status: (q.filters.status as SaleStatus) || undefined,
    fromUtc: q.filters.from ? new Date(q.filters.from + 'T00:00:00').toISOString() : undefined,
    toUtc: q.filters.to ? new Date(q.filters.to + 'T23:59:59').toISOString() : undefined,
  }),
})
```

Table: Sale # (`Link` to `/sales/{id}`, bold) / Date (`new Date(createdAtUtc).toLocaleString()`) / Items (`itemCount`) / Total (`formatMoney(grandTotal)`, right) / Payment (`paymentSummary`) / Status (`<StatusBadge>`). `Pagination`. Skeleton + `ErrorState` + `EmptyState` (icon `ClipboardList`, "No sales yet" / "Sales you ring up in the POS will show here.") exactly like `InventoryPage`.

Branch/cashier filters: **skip** — one branch, one cashier per tenant today (spec §14). (No register filter either unless there is a quick win — skip.)

- [ ] **Step 2: Verify — build + smoke**

`npm run lint && npm run build` → pass. Interactive: `/sales` lists the sales made in Tasks 8–9; search by sale number filters; status filter works; row → `/sales/:id` (blank until Task 11).

- [ ] **Step 3: Commit**

```bash
git add web/negosio-web/src/pages/SalesPage.tsx
git commit -m "feat(pos): sales history list with filters"
```

---

## Task 11: Sale detail + returns list

**Files:**
- Rewrite: `src/pages/SaleDetailPage.tsx`
- Create: `src/components/sales/SaleItemsTable.tsx`
- Create: `src/components/sales/SaleReturnsList.tsx`

**Interfaces:**
- Consumes: `salesApi`; `useCan`; `Table`, `Button`, `Badge`, `Callout`, `EmptyState`, `ErrorState`, `SkeletonText`; `StatusBadge`; `formatMoney`, `formatQty`; `PAYMENT_METHOD_LABELS`, `SALE_STATUS_LABELS`; `useParams`, `Link`, `useNavigate`.
- Produces:
  - `SaleItemsTable({ items }: { items: SaleItemDto[] })`
  - `SaleReturnsList({ returns }: { returns: SaleReturnDto[] })`
  - `hasReturnableQty(items: SaleItemDto[]): boolean` (exported from `SaleItemsTable.tsx` or a small `src/lib/returns.ts` — pick one; if `src/lib/returns.ts`, Task 12 also imports `returnableQty(item)`). **Decision: create `src/lib/returns.ts` now** with `returnableQty(i: SaleItemDto) => i.quantity - i.returnedQuantity` and `hasReturnableQty(items)`.

- [ ] **Step 1: `src/lib/returns.ts`**

```ts
import type { SaleItemDto } from '../api/types'
export const returnableQty = (i: SaleItemDto) => i.quantity - i.returnedQuantity
export const hasReturnableQty = (items: SaleItemDto[]) => items.some((i) => returnableQty(i) > 0)
```

- [ ] **Step 2: `SaleItemsTable.tsx`**

`Table` columns: Item (productName + variantName + sku muted) / Qty (`formatQty`) / Unit (`formatMoney`) / Gross / Discount / Tax / Net (all `formatMoney`, right) / Returned (`formatQty(returnedQuantity)` when `> 0`, else "—"). Footer-ish totals are on the page, not here.

- [ ] **Step 3: `SaleReturnsList.tsx`**

If `returns.length === 0` → nothing (page decides whether to show a heading). Else a stacked list: each return card — `RET-…` number + date + `createdByName`, reason, a small line table (productName / qty / refund), refund method(s) from `refunds` (`{method} {formatMoney(amount)}`), **Total refund** `formatMoney(totalRefund)`.

- [ ] **Step 4: `SaleDetailPage.tsx`**

`DashboardLayout title="Sale"`. `useQuery(['sales', id], () => salesApi.get(id))`.
Header: `Sale {sale.saleNumber}` + `<StatusBadge>` + muted line "{date} · {cashierName} · {branchName}".
Totals panel (right or below): Subtotal / Discount / Tax / **Total** / Amount paid / Change due (`formatMoney`).
`<SaleItemsTable items={detail.items} />`.
Payments block: each `payment` → `{PAYMENT_METHOD_LABELS[method]} · {formatMoney(amount)}` (+ received/change for cash).
Returns: `<h2>Returns</h2>` + `<SaleReturnsList>` when `detail.returns.length`.
Actions row: **Print receipt** (`Link` to `/sales/{id}/receipt`), and **Start return** button — rendered only when `useCan('refund:manage') && (sale.status === 'Completed' || sale.status === 'PartiallyRefunded') && hasReturnableQty(detail.items)` → opens `ReturnModal` (Task 12; until then the button can be present but disabled with a TODO — **no**, per no-placeholder rule: land the button in Task 12 instead. In Task 11 render everything except the button.)

- [ ] **Step 5: Verify — build + smoke**

`npm run lint && npm run build` → pass. Interactive: `/sales/:id` for a real sale shows lines, totals matching the receipt, payments; no Returns section yet.

- [ ] **Step 6: Commit**

```bash
git add web/negosio-web/src/pages/SaleDetailPage.tsx web/negosio-web/src/components/sales/SaleItemsTable.tsx web/negosio-web/src/components/sales/SaleReturnsList.tsx web/negosio-web/src/lib/returns.ts
git commit -m "feat(pos): sale detail page with items, payments and returns list"
```

---

## Task 12: Returns modal

**Files:**
- Create: `src/components/sales/ReturnModal.tsx`
- Modify: `src/pages/SaleDetailPage.tsx` (add the gated "Start return" button + modal)

**Interfaces:**
- Consumes: `salesApi.createReturn`; `returnableQty` from `src/lib/returns`; `POS_PAYMENT_METHODS`, `PAYMENT_METHOD_LABELS`; `Modal`, `Button`, `TextField`, `Select`, `Callout`, `Table`, `useToast`; `formatMoney`, `formatQty`; `ApiError`; `useMutation`, `useQueryClient`.
- Produces:
  - `ReturnModal({ open, onClose, sale }: { open: boolean; onClose: () => void; sale: SaleDetailDto })`

- [ ] **Step 1: `ReturnModal.tsx`**

Local state: `qty: Record<saleItemId, string>` (default `''`), `restock: Record<saleItemId, boolean>` (default `true`), `reason`, `refundMethod: PaymentMethod` (default `'Cash'`), `refundReference`. Reset in `useEffect` on `open`.

Body: a `Table` — one row per `sale.items` with `returnableQty(item) > 0`:
- Item (name + variant)
- Purchased (`formatQty(quantity)`)
- Returned (`formatQty(returnedQuantity)`)
- Returnable (`formatQty(returnableQty(item))`)
- Return qty — `<input type="number" step="0.001" min={0} max={returnableQty(item)}>` bound to `qty[item.id]`
- Restock — `<input type="checkbox">` bound to `restock[item.id]`

Below: **Reason** textarea (required, maxLength 500), **Refund method** `Select`, **Refund reference** `TextField` (optional).

Running **estimated refund**: for each row with `n = Number(qty[id]) > 0`, `refund ≈ (item.netAmount + (taxExclusive ? item.taxAmount : 0)) * n / item.quantity` — but we don't have tax-inclusive flag here without `useTaxSettings`; **use `useTaxSettings()`** to get `pricesIncludeTax`. Sum, `roundMoney`, show as "Estimated refund: {formatMoney(x)} (final amount confirmed by the server)".

Submit → build `items` = rows where `Number(qty[id]) > 0` → `{ saleItemId: id, quantity: Number(qty[id]), restock: restock[id] }`. Guard: at least one item, each `<= returnableQty`, reason non-empty (inline errors, don't rely only on backend).

`useMutation` → `salesApi.createReturn(sale.sale.id, body)`:
- `onSuccess` → `qc.invalidateQueries({ queryKey: ['sales', sale.sale.id] })`, `qc.invalidateQueries({ queryKey: ['sales'] })`, `qc.invalidateQueries({ queryKey: ['inventory'] })`, `qc.invalidateQueries({ queryKey: ['dashboard'] })`, toast success, `onClose()`.
- `onError` `ApiError`:
  - `RETURN_QUANTITY_EXCEEDED` / `RETURN_NOT_ALLOWED` → `Callout tone="warning"` + `qc.invalidateQueries({ queryKey: ['sales', sale.sale.id] })` (parent refetches; modal may now show fewer rows on reopen).
  - `VALIDATION_FAILED` → map `error.fieldErrors` to inline messages where possible, else a `Callout`.
  - else → toast error.

- [ ] **Step 2: Wire into `SaleDetailPage.tsx`**

Add the button (gating from Task 11 step 4) + `const [returnOpen, setReturnOpen] = useState(false)` + `<ReturnModal open={returnOpen} onClose={() => setReturnOpen(false)} sale={detail} />`.

- [ ] **Step 3: Verify — build + interactive smoke**

`npm run lint && npm run build` → pass.

Interactive (logged in as Owner — has `refund:manage`):
- On a `Completed` sale with 2× Widget A, **Start return** → modal lists Widget A, Returnable 2.
- Return qty 1, restock checked, reason "customer changed mind", method Cash → submit.
- Toast; modal closes; sale status → **Partially refunded**; Returns section shows `RET-MAIN-000001`, refund amount; `SaleItemsTable` "Returned" column shows 1.
- Open `/inventory` → Widget A on-hand increased by 1; `/inventory/movements` shows a `Return` row.
- Reopen the return modal → Widget A now Returnable 1; try qty 5 → inline "cannot exceed 1" (and if bypassed, backend `RETURN_QUANTITY_EXCEEDED` → callout).

- [ ] **Step 4: Commit**

```bash
git add web/negosio-web/src/components/sales/ReturnModal.tsx web/negosio-web/src/pages/SaleDetailPage.tsx
git commit -m "feat(pos): returns modal — partial/full returns from sale detail"
```

---

## Task 13: Full verification pass

**Files:** none (verification + fixes only; any fix gets its own small commit).

- [ ] **Step 1: Backend tests unaffected**

Kill any stale `Negosio.Api` / `dotnet run` (port 5170) first.
Run: `dotnet test Negosio.sln`
Expected: 50 unit + 99 integration, 0 failed (no backend files changed, so this is a regression guard only).

- [ ] **Step 2: Frontend lint + build**

Run: `cd web/negosio-web && npm run lint && npm run build`
Expected: both clean. Note bundle size in the commit message if it grew notably.

- [ ] **Step 3: End-to-end browser walkthrough (headless Chrome + CDP, real API)**

Start API (Development launch profile) + `npm run dev`. Log in as `invui@example.com` / `SecurePassword123!`. Walk the whole vertical and screenshot each step to the session scratchpad:

1. `/registers` → create register, **open session** (opening cash 2000).
2. `/pos` → auto-selects the register, session live. Search "Widget", add Widget A ×2 and Widget B ×1. Apply a 10% discount to Widget A. Scan a barcode (CDP keydown burst) for another unit.
3. **Charge** → Cash, received 3000 → change shown → **Confirm** → `/pos/complete/:id` (invoice #, paid, change). Cart cleared, storage key gone.
4. **Print receipt** → receipt renders at 80 mm; `window.print()` fires with `?print=1`; no console errors.
5. `/sales` → the sale is listed; open it → totals match the receipt.
6. **Start return** → return 1× Widget A, restock, reason → success → status **Partially refunded**; `/inventory` reflects +1; movement ledger shows `Return`.
7. **Idempotency:** new cart → PaymentModal open/close keeps the same `clientRequestId` (inspect via a `console.log` temporarily or React DevTools); CDP offline → Confirm → network message → online Retry → exactly one new sale in `/sales`.
8. **INSUFFICIENT_INVENTORY:** set Widget B stock to 1 (`/inventory` adjust), cart qty 3 → Confirm → modal closes, cart callout, grid stock refreshes; drop qty to 1 (id rotates) → Confirm → succeeds.
9. **Session-lost:** close the session from `/registers` while `/pos` is open in another tab, then Charge/Confirm in the POS tab → drops to the session gate, cart preserved.
10. RBAC affordance: (can't fully test without a non-owner user — note that `useCan` gates are in place: `refund:manage` hides Start return, `register:manage` hides New register, `pos:operate` gates POS).

- [ ] **Step 4: Fix anything the walkthrough surfaces**

Each fix = its own `fix(pos): …` commit. Re-run steps 2–3 after fixes.

- [ ] **Step 5: Update the handover**

Rewrite `handover.md` (repo root) to record: POS frontend built on `feature/pos-frontend`, what's verified, the working dev login, remaining gaps (multi-branch untested, split tender / order discounts deferred, dashboard/tax-settings still Phase-1). Do **not** merge to `master` — leave that to the user.

```bash
git add handover.md
git commit -m "docs: handover — POS frontend vertical complete on feature/pos-frontend"
```

- [ ] **Step 6: Report to the user**

Summarize: build order completed, verification results (test counts, lint/build, walkthrough outcomes), screenshots location, and that `feature/pos-frontend` is ready for their review + merge decision. Offer `superpowers:requesting-code-review` and `superpowers:finishing-a-development-branch`.

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task(s) |
|---|---|
| §1 goals 1–7 | 4 / 3 / 6–8 / 8 / 9 / 10–11 / 12 |
| §2 contract + invariants | 1 (types/api), enforced throughout |
| §3 routes | 1 (stubs + wiring), filled 4/6/9/10/11 |
| §4 API modules & types | 1 |
| §4 `useCan` caps | 1 |
| §4 `src/lib/pos.ts` | 1 |
| §4 `saleMath.ts` preview-only | 2 |
| §5 Registers components | 4 |
| §5 session modals | 3 |
| §5 POS shell / picker / gate | 6 |
| §5 terminal / search / grid / cart / discount popover | 7 |
| §5 PaymentModal | 8 |
| §5 PosCompletePage | 9 |
| §5 Sales / SaleDetail / ReturnModal / ReceiptPage | 10 / 11 / 12 / 9 |
| §6 register/session UX + no-quick-sell | 4 / 6 |
| §7 cart state + scoped persistence + register persistence | 5 / 6 / 7 |
| §8 barcode strategy | 5 (hook) / 7 (wiring) |
| §9 checkout / idempotency lifecycle | 8 |
| §10 payment UX | 8 |
| §11 receipt / print CSS | 9 |
| §12 returns workflow | 12 |
| §13 build order | tasks are in this order |
| §14 non-goals | not implemented (correct) |
| "run backend tests + lint/build + full browser flow" | 13 |

No gaps.

**2. Placeholder scan:** No "TBD"/"implement later". Task 11 step 4 explicitly defers the "Start return" button to Task 12 rather than leaving a stub. Task 7 explicitly scopes the checkout wiring out to Task 8 with a named no-op. Receipt/print CSS is given in full. `clientRequestId` lifecycle is spelled out as concrete state + rules.

**3. Type consistency:**
- `TerminalCtx` / `CartLine` defined in Task 5 (`posStorage.ts`), imported by Tasks 6–8.
- `registersApi` / `sessionsApi` / `posCatalogApi` / `checkoutApi` / `salesApi` / `taxSettingsApi` defined Task 1, method signatures unchanged where reused.
- `PosCatalogItemDto`, `SaleResultDto`, `SaleDetailDto`, `ReceiptDto`, `CreateReturnRequest` field names match the C# records read from the backend (spec §2 / verified in source).
- `saleStatusTone` returns the `Badge` tone subset (`neutral|success|warning|danger`) — matches `Badge`'s `BadgeTone`.
- `usePosCart` API (`addItem/setQty/removeLine/setLineDiscount/clear/lines/isEmpty`) consistent between Task 5 definition and Task 7/8 use.
- `useCan` capability strings identical everywhere: `register:manage`, `pos:operate`, `sales:view`, `refund:manage`.
- `returnableQty` / `hasReturnableQty` defined once in `src/lib/returns.ts` (Task 11), reused Task 12.

No inconsistencies found.
