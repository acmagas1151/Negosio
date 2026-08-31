# POS Frontend (Phase 3) — Design

> Date: 2026-08-31 · Branch: `feature/pos-frontend`
> Status: approved (with four refinements, folded in below)
> Backend: **no changes required** — every screen maps to an existing, integration-tested endpoint.

---

## 1. Goal & scope

Catch the browser UI up to the Phase 3 retail-POS backend. Deliver, in this order:

1. **Registers** — list / create / edit / deactivate, branch assignment, live session state.
2. **Register sessions** — open (opening cash), view current, close (expected / counted /
   difference). No "quick sell without a session".
3. **POS** (`/pos`) — dedicated full-screen terminal: product search (name / SKU / barcode),
   USB barcode-scanner support, persistent cart, quantity controls, line-level discounts,
   subtotal / tax / total **preview**, single-tender payment modal with cash-change handling.
4. **Checkout reliability** — one stable `clientRequestId` per checkout attempt, retained across
   retries of an uncertain / failed request; duplicate-submission guard; `INSUFFICIENT_INVENTORY`
   handling; cart preserved on network failure.
5. **Sale completion + receipt** — success screen (invoice number, paid, change), New Sale /
   View Sale / Print Receipt. Browser print CSS, **no PDF generation**.
6. **Sales** (`/sales`, `/sales/:id`) — list / filter / paginate; historical detail from backend
   snapshots.
7. **Returns** — started from Sale Detail; partial / full; shows purchased vs already-returned;
   backend authoritative; refresh sale + inventory queries after success.

**Out of scope (do not touch):** Dashboard metrics, Tax Settings page, Staff / user management,
F&B, offline sync, Azure, any backend architecture change.

### Reuse (do not reinvent)

`Modal`, `ConfirmDialog`, `Table`, `Pagination`, `SearchInput`, `Select`, `Button`, `Callout`,
`EmptyState`, `ErrorState`, `SkeletonText`, `useToast`, `usePagedQuery`, `useCan`, `formatMoney`,
`formatQty`, TanStack Query. **No Redux / Zustand** — cart is a single-screen concern held in a
`useReducer` hook plus `localStorage` for durability.

---

## 2. Backend contract (reference — verified against code)

| Need | Endpoint | Policy → roles |
|---|---|---|
| Register list / get | `GET /api/registers?branchId=&isActive=&page=&pageSize=`, `GET /api/registers/{id}` | `[Authorize]` any tenant user |
| Register create / update / deactivate | `POST /api/registers`, `PUT /api/registers/{id}`, `DELETE /api/registers/{id}` | `RegisterManage` → Owner / Admin / Manager |
| Branch feed | `GET /api/branches` | any tenant user |
| Open session | `POST /api/register-sessions/open` `{ registerId, openingCash }` | `PosOperate` → Owner / Admin / Manager / Cashier |
| Current session | `GET /api/register-sessions/current?registerId=&branchId=` | `PosOperate` |
| Close session | `POST /api/register-sessions/{id}/close` `{ closingCash }` | `PosOperate` |
| POS catalog search | `GET /api/pos/catalog?branchId=&search=&page=&pageSize=` | `PosOperate` |
| Barcode lookup | `GET /api/pos/catalog/barcode/{barcode}?branchId=` | `PosOperate` |
| Checkout | `POST /api/pos/checkout` `{ branchId, registerSessionId, clientRequestId, items[], payments[] }` → `SaleResultDto` | `PosOperate` |
| Sales list / detail | `GET /api/sales?branchId=&registerId=&cashierUserId=&status=&fromUtc=&toUtc=&search=&page=&pageSize=`, `GET /api/sales/{id}` | `SalesView` → Owner / Admin / Manager / Cashier |
| Receipt | `GET /api/sales/{id}/receipt` → `ReceiptDto` | `SalesView` |
| Returns list / create | `GET /api/sales/{id}/returns`, `POST /api/sales/{id}/returns` `{ items[], reason, refundMethod, refundReference }` | list `SalesView`; create `RefundManage` → Owner / Admin / Manager |

### Invariants the UI must respect

- **Server re-prices everything.** Checkout takes only `productVariantId` + `quantity` +
  optional per-line `discount`. It recomputes gross / discount / tax / totals from
  `variant.SellingPrice` and the tenant's tax settings. The cart's numbers are a **preview**;
  `SaleResultDto` is authoritative.
- **Idempotency:** a repeat `clientRequestId` returns the original sale with
  `wasExistingRequest: true` at **HTTP 200** (not an error).
- **HTTP status mapping:** `INSUFFICIENT_INVENTORY`, `CHECKOUT_CONCURRENCY_CONFLICT`,
  `REGISTER_SESSION_ALREADY_OPEN` → **409**. `VALIDATION_FAILED`, `INVALID_SALE_ITEM`,
  `INVALID_QUANTITY`, `INVALID_DISCOUNT`, `INVALID_PAYMENT`, `PAYMENT_INSUFFICIENT`,
  `REGISTER_SESSION_NOT_OPEN` → **400**. `BRANCH_NOT_FOUND`, `REGISTER_SESSION_NOT_FOUND`,
  `PRODUCT_NOT_FOUND` (barcode), `REGISTER_NOT_FOUND`, `SALE_NOT_FOUND` → **404**.
- **Registers per branch:** a branch may have **many** registers. One **open session per
  register** (filtered unique index → `REGISTER_SESSION_ALREADY_OPEN`).
  `GET /api/register-sessions/current` resolves by `registerId` (preferred), else `branchId`,
  else only if the tenant has exactly one branch. **The UI resolves by `registerId`.**
- **Discounts are per-line only.** No order-level discount field exists.
- **Returns:** allowed only on `Completed` / `PartiallyRefunded` sales.
  `SaleItemDto.returnedQuantity` gives already-returned qty; returnable = `quantity −
  returnedQuantity`. Over-return → `RETURN_QUANTITY_EXCEEDED`. Restock happens server-side only
  for inventory-tracked variants regardless of the client's `restock` flag.
- **Single-branch reality today:** `GET /api/branches` returns one branch; branch selectors
  auto-pick it and the branch column/filter is hidden when `length <= 1`.
- `PosCatalogItemDto.isAvailable` = `!trackInventory || quantityAvailable > 0`.

---

## 3. Routes

In-dashboard (`DashboardLayout` + sidebar):

| Route | Page |
|---|---|
| `/registers` | Register list; create / edit / deactivate; per-row live session state + open/close actions |
| `/sales` | Sales history list (filters, pagination) |
| `/sales/:id` | Sale detail — lines, payments, returns; Print receipt; Start return |
| `/sales/:id/receipt` | Print-optimised receipt view (`?print=1` auto-invokes `window.print()`) |

Full-screen (rendered **outside** `DashboardLayout` — no sidebar):

| Route | Page |
|---|---|
| `/pos` | Checkout terminal; renders the session gate when no open session for the selected register |
| `/pos/complete/:saleId` | Sale-complete success screen |

Routing: add the in-dashboard routes to the existing `protectedRoutes` array in `App.tsx`.
The two `/pos*` routes are also protected but render their own shell (no `DashboardLayout`).

Nav (`src/lib/nav.ts`): new group **"Point of sale"** → `POS` (`/pos`, `ShoppingCart`),
`Registers` (`/registers`, `Calculator`), `Sales` (`/sales`, `ClipboardList`). Remove the
matching "Soon" placeholders. `Reports` / `Staff` / `Settings` stay disabled.

---

## 4. API modules & types

### `src/api/pos.ts`

- `registersApi`: `list(params)`, `get(id)`, `create(body)`, `update(id, body)`, `deactivate(id)`
- `sessionsApi`: `open(body)`, `current(params: { registerId?, branchId? })`, `close(id, body)`
- `posCatalogApi`: `search(params)`, `barcode(code, branchId)`
- `checkoutApi`: `checkout(body)`
- `salesApi`: `list(params)`, `get(id)`, `receipt(id)`, `listReturns(id)`, `createReturn(id, body)`

Follows the existing `apiRequest` + `qs()` pattern from `src/api/inventory.ts` / `catalog.ts`.

### `src/api/types.ts` additions

String-union enums (match existing style):

- `PaymentMethod = 'Cash' | 'Card' | 'GCash' | 'Maya' | 'BankTransfer' | 'Other'`
- `DiscountType = 'None' | 'FixedAmount' | 'Percentage'`
- `SaleStatus = 'Completed' | 'Voided' | 'Refunded' | 'PartiallyRefunded'`
- `RegisterSessionStatus = 'Open' | 'Closed'`

DTOs / requests (fields mirror the C# records exactly):

- `RegisterDto`, `CreateRegisterRequest`, `UpdateRegisterRequest`, `RegisterListParams`
- `RegisterSessionDto`, `OpenRegisterSessionRequest`, `CloseRegisterSessionRequest`
- `PosCatalogItemDto`, `PosCatalogParams`
- `CheckoutDiscountInput`, `CheckoutItemInput`, `CheckoutPaymentInput`, `CheckoutRequest`,
  `SaleResultDto`
- `SaleItemDto`, `SalePaymentDto`, `SaleSummaryDto`, `SaleDetailDto`, `SaleListParams`
- `ReceiptDto`, `ReceiptLineDto`, `ReceiptPaymentDto`
- `ReturnLineInput`, `CreateReturnRequest`, `SaleReturnDto`, `SaleReturnItemDto`

### `src/lib/useCan.ts`

Add capabilities mirroring `AuthorizationPolicies.cs`:

- `register:manage` → Owner / Admin / Manager
- `pos:operate` → Owner / Admin / Manager / Cashier
- `sales:view` → Owner / Admin / Manager / Cashier
- `refund:manage` → Owner / Admin / Manager

### `src/lib/pos.ts`

`PAYMENT_METHOD_LABELS`, `SALE_STATUS_LABELS`, `saleStatusTone()` (badge tone),
`suggestCashButtons(total)` → ordered denominations (exact, next 50 / 100 / 500 / 1000 up).

### `src/lib/saleMath.ts` — **preview only**

Minimal re-implementation of `SaleLineCalculator` (half-up round at 2dp, per-line then summed;
tax exclusive by default, informational when `pricesIncludeTax`). Consumes tax settings from
`GET /api/settings/tax` (read-only fetch, cached with a long `staleTime`; **this is not the Tax
Settings page** — no write UI). Used solely to render the cart summary before checkout.
**Never** treated as authoritative: the charge button shows the preview total, the success
screen and sale detail show `SaleResultDto` / `SaleDetailDto` values, and any mismatch is
resolved in favour of the server with no client reconciliation.

---

## 5. Component breakdown

### Registers — `src/pages/RegistersPage.tsx`, `src/components/registers/`

- `RegisterFormModal` — create / edit. Fields: name, code, branch (`Select`, auto-selected and
  hidden when one branch), `isActive` toggle (edit only). `409 REGISTER_ALREADY_EXISTS` →
  inline error on the code field.
- `RegisterSessionCell` — per row; `useQuery(['session','current',registerId])` →
  `sessionsApi.current({ registerId })`. Shows `Open · ₱{openingCash} · since {time}` or
  `No open session`. Actions: **Open session** / **Close session** launching the shared modals.
- Deactivate → `ConfirmDialog`; warn in the dialog body if that register currently has an open
  session (deactivation still allowed — backend only flips `IsActive`).

### Session modals — `src/components/pos/` (shared by Registers and POS)

- `OpenSessionModal` — props `{ registerId, registerName, onOpened }`. One field: opening cash
  (`>= 0`). Submit → `sessionsApi.open`. `409 REGISTER_SESSION_ALREADY_OPEN` → callout + refetch.
- `CloseSessionModal` — props `{ session, onClosed }`. One field: counted closing cash (`>= 0`).
  On success the modal swaps to a **read-only reconciliation summary**: Opening / Expected /
  Counted / **Difference** (over = positive/success tone, short = negative/danger tone), then
  Done. Values come from `RegisterSessionDto` (`expectedCash`, `cashDifference`).

### POS shell — `src/pages/PosPage.tsx`, `src/components/pos/`

- `PosShell` — full-height flex column, own chrome, not `DashboardLayout`.
  - `PosTopBar` — store name (`user.tenantName`), selected register + session summary
    (opening cash, opened-at, cashier `user.firstName`), overflow menu (Close session), and
    **Exit** → `/dashboard`.
  - Body: `RegisterPicker` → `PosSessionGate` → `PosTerminal`, gated on state (§6).
- `RegisterPicker` — shown when there is **no persisted/derived register selection** and more
  than one active register. Lists active registers (`registersApi.list({ isActive: true })`).
  Exactly one active register → auto-select, skip. Zero → empty state linking to `/registers`
  (gated: only `register:manage` can create). Selection persisted (§7).
- `PosSessionGate` — shown when `sessionsApi.current({ registerId })` 404s. Renders
  `OpenSessionModal` content inline (register fixed to the selection), plus a "Choose a
  different register" link that clears the selection.
- `PosTerminal` — `lg:grid lg:grid-cols-[62%_38%]`; below `lg` the cart stacks under the
  product panel.
  - **Product panel:** `PosSearchBar` (autofocus, 300 ms debounce; Enter triggers a barcode
    lookup first, falling back to search), `PosProductGrid` (card buttons: product name,
    variant name, price, stock chip; `disabled` when `!isAvailable`; click → `addItem`),
    `Pagination`.
  - **Cart panel:** `CartList` → `CartLineRow` (name / variant, unit price, qty stepper
    (− / value / +), line-discount button, line total, remove ✕); `CartSummary` (subtotal,
    discount, tax, **grand total** — all from `saleMath`, labelled "estimated"); **Charge
    ₱{total}** button (disabled when cart empty or a checkout is in flight).
  - `LineDiscountPopover` — `Select` type (None / % / ₱) + value input; writes
    `{ type, value }` onto the line.
  - `PaymentModal` — §8.
- `PosCompletePage` (`/pos/complete/:saleId`) — reads `SaleResultDto` handed via router state,
  falls back to `salesApi.get(saleId)` on hard refresh. Big check icon; sale number; grand
  total; amount paid; **change due**. Buttons: **New sale** (→ `/pos`, cart already cleared),
  **View sale** (`/sales/:saleId`), **Print receipt** (`/sales/:saleId/receipt?print=1`).

### Sales — `src/pages/SalesPage.tsx`, `src/pages/SaleDetailPage.tsx`, `src/components/sales/`

- `SalesPage` — `usePagedQuery` with `DEFAULT_FILTERS` as a module-level const. Filters:
  search (sale number), `status`, date range (`fromUtc` / `toUtc` whole-day bounds), register
  (only when >1 register), cashier + branch (only when >1). `Table` columns: Sale # / Date /
  Items / Total / Payment / Status badge. Row → `/sales/:id`.
- `SaleDetailPage` — header (sale #, `StatusBadge`, date, cashier, register/session),
  `SaleItemsTable` (qty, unit, gross, discount, tax, net, returned-qty), payments block
  (method, amount, received, change), `SaleReturnsList` (each return: number, date, reason,
  by, line items, refund). Actions: **Print receipt**; **Start return** — visible only when
  `useCan('refund:manage')` **and** status ∈ {Completed, PartiallyRefunded} **and** some line
  has returnable qty > 0.
- `ReturnModal` — §11.
- `ReceiptPage` (`src/pages/ReceiptPage.tsx`) — §10.

---

## 6. Register / session UX

- **No quick-sell escape hatch.** Backend requires an open session; UI enforces the same.
- POS state machine on `/pos`:
  1. **Resolve register** — read persisted selection (§7); validate it against
     `registersApi.list({ isActive: true })`. If invalid/absent and exactly one active
     register → auto-select it. Otherwise show `RegisterPicker`.
  2. **Resolve session** — `sessionsApi.current({ registerId })`.
     - **404** → `PosSessionGate` (open session with opening cash).
     - **200** → `PosTerminal`; `registerSessionId` in memory for checkout.
  3. Session ends mid-use (checkout returns `REGISTER_SESSION_NOT_FOUND` /
     `REGISTER_SESSION_NOT_OPEN`) → drop to step 2, **cart preserved**.
- **Closing** from `/registers` row action or the POS top-bar overflow. `CloseSessionModal`
  shows the reconciliation summary from the backend response.
- Opening a second session on a register that already has one → `409` → callout + refetch
  current (which will now return the open one).
- Ceremony is two number inputs total. Cashier identity = logged-in user (no staff model).

---

## 7. Cart state & persistence

- **`usePosCart` hook** — `useReducer`. State:
  `{ lines: Array<{ variantId, productId, name, variantName, sku, unitPrice, quantity,
  discount: { type: DiscountType, value: number } }> }`.
  `unitPrice` / `name` are display snapshots from `PosCatalogItemDto`; the server re-prices.
- Actions: `addItem(item)` (bumps qty if the variant is present), `setQty(variantId, n)`
  (removes at 0), `removeLine(variantId)`, `setLineDiscount(variantId, discount)`, `clear()`.
- **Persistence key is scoped to the terminal, not just the branch:**
  `negosio.pos.cart.v1.<tenantId>/<branchId>/<registerSessionId>`. Written on every change
  (try/catch), rehydrated when a session is resolved. A different register or a new session
  gets a different key, so **carts cannot leak between registers or sessions**. On resolving a
  session, stale cart keys for other sessions of the same register are pruned
  (best-effort `localStorage` sweep).
- Cart cleared on **confirmed checkout success** (once a `saleId` exists) — its key removed.
- **Preview totals** via `src/lib/saleMath.ts` (§4) — estimated, never authoritative.
- Stock is shown but **not** enforced client-side (matches the Inventory-UI decision). The
  checkout `409 INSUFFICIENT_INVENTORY` is the real guard.
- The **selected register id** is persisted separately:
  `negosio.pos.register.v1.<tenantId>/<branchId>` — convenience only, re-validated on load.

---

## 8. Barcode strategy

- `useBarcodeScanner({ onScan, enabled })` — window `keydown` listener (USB scanners are
  keyboard wedges):
  - Buffer printable chars; reset the buffer if the inter-keystroke gap exceeds ~50 ms.
  - `Enter` with buffer length ≥ 3 → `preventDefault`, `onScan(buffer)`, clear.
  - Suppressed while focus is in an `input` / `textarea` **inside a modal**; still active when
    focus is in the POS search box (so scanning into it works).
- `onScan(code)` → `posCatalogApi.barcode(code, branchId)`:
  - hit → `addItem` + toast/flash of the added line.
  - `404 PRODUCT_NOT_FOUND` → short non-blocking toast `No product for barcode {code}`.
- Search box Enter runs the same lookup, falling back to a text search on 404.
- No hardware config screen.

---

## 9. Checkout / idempotency

- **`clientRequestId` is owned by the checkout *attempt*, which lives with cart state — not the
  `PaymentModal`.** Held in a `useRef` inside `PosTerminal` (or the cart hook), seeded with
  `crypto.randomUUID()` when the cart goes from empty → non-empty.
  - **Closing and reopening `PaymentModal` retains the same id.**
  - A **new id** is generated only:
    1. after a **confirmed successful** checkout (next sale), or
    2. after the order **meaningfully changes following a definitive (non-transient)
       rejection** — e.g. the cashier edits the cart after an `INSUFFICIENT_INVENTORY` /
       `INVALID_SALE_ITEM` / `INVALID_QUANTITY` / `INVALID_DISCOUNT` rejection.
  - Transient / uncertain outcomes (network error, `CHECKOUT_CONCURRENCY_CONFLICT`) **keep**
    the id so a retry dedupes server-side.
- **Duplicate-submission guard** — `status: 'idle' | 'submitting' | 'failed' | 'succeeded'`.
  Charge / Confirm / Retry disabled while `submitting`. `useMutation` keyed by the
  `clientRequestId`.
- **Outcomes:**
  | Result | UI |
  |---|---|
  | `200` (any `wasExistingRequest`) | clear cart + its storage key; `navigate('/pos/complete/'+saleId, { state: result })`; invalidate `['inventory']`, `['sales']`, `['dashboard']` |
  | `NETWORK_ERROR` (status 0) | `status=failed`; keep modal open, keep cart, **keep id**; message "We couldn't confirm the sale — check your last sale, then Retry"; **Retry** + **Check last sale** (`/sales`) |
  | `409 INSUFFICIENT_INVENTORY` | `status=failed`; close payment modal; cart-level `Callout`; refetch POS catalog (stock chips update); cashier adjusts qty → re-charge with a **fresh id** |
  | `409 CHECKOUT_CONCURRENCY_CONFLICT` | transient; "Please try again"; keep cart + **same id**; Retry |
  | `400 PAYMENT_INSUFFICIENT` / `INVALID_PAYMENT` | inline error inside `PaymentModal` |
  | `400 INVALID_SALE_ITEM` / `INVALID_QUANTITY` / `INVALID_DISCOUNT` | cart `Callout`; prompt to review the line; next cart edit rotates the id |
  | `404 REGISTER_SESSION_NOT_FOUND` / `400 REGISTER_SESSION_NOT_OPEN` | drop to session gate, **cart preserved** |

---

## 10. Payment UX

- One `PaymentModal`, **single tender**:
  - **Amount due** shown large (preview grand total).
  - Method segmented buttons: Cash / Card / GCash / Maya / Bank transfer.
  - **Cash:** "Cash received" field + quick-cash buttons (`suggestCashButtons`); live **Change**
    = received − total (₱0.00 until ≥ total); **Confirm** disabled until received ≥ total.
    Sends `{ method: 'Cash', receivedAmount }`.
  - **Non-cash:** optional reference-number field; **Confirm** enabled immediately.
    Sends `{ method, amount: <preview total>, referenceNumber }`.
  - **Confirm payment** → checkout (§9); disabled while `submitting`.
- `payments` array is always length 1. `SaleResultDto.changeDue` is the change shown on the
  success screen (server truth), not the modal's live preview.

---

## 11. Receipt / printing

- **Browser print only. No PDF, no library.**
- `ReceiptPage` at `/sales/:id/receipt` → `salesApi.receipt(id)` → `ReceiptDto`. Layout: 80 mm
  column — store name, branch, register, sale #, cashier, timestamp; line items
  (name / variant, `qty × unit`, net); subtotal / discount / tax / **total**; payments;
  **change**; a `Refunded` / `PartiallyRefunded` banner when applicable; thank-you line.
- Scoped `@media print` block: hide app chrome, `width: 80mm`, black-on-white, no shadows,
  zero page margin. `@media screen`: centred on a grey backdrop with **Print** + **Back**.
- Opened with `?print=1` → calls `window.print()` once after fonts settle.
- **No auto-print after checkout** — cashier taps Print (avoids surprise dialogs and double
  prints on idempotent retries).

---

## 12. Returns workflow

- Entry: **Sale Detail → "Start return"** (gated: `refund:manage` + status ∈ {Completed,
  PartiallyRefunded} + some line returnable).
- `ReturnModal`:
  - Row per sale item: product, **purchased qty**, **already returned**
    (`returnedQuantity`), **returnable** (`quantity − returnedQuantity`), qty input capped at
    returnable (client hint), **restock** checkbox (default on; backend still decides actual
    restock by tracked-ness).
  - **Reason** (required, ≤ 500), **refund method** (same selector as payment), optional
    **refund reference**.
  - Running **refund total** preview (prorate `net (+ tax when tax-exclusive)` by qty) —
    labelled approximate; `SaleReturnDto.totalRefund` is authoritative.
  - Submit → `salesApi.createReturn(id, body)`.
    - `RETURN_QUANTITY_EXCEEDED` / `RETURN_NOT_ALLOWED` → callout + refetch sale.
    - validation → field errors.
  - Success → toast; close; **invalidate `['sales', id]`, `['inventory']`, `['dashboard']`**.
    Sale status flips via the refetch.
- Partial and full use the same flow; multiple sequential returns supported.
- Backend authoritative for refund amounts, restock, numbering, status transitions.

---

## 13. Build order

1. **Types / API / RBAC / nav** — `src/api/types.ts`, `src/api/pos.ts`, `useCan` caps,
   `src/lib/pos.ts`, `src/lib/saleMath.ts`, `src/lib/nav.ts`, `App.tsx` routes.
2. **Registers + session modals** — `RegistersPage`, `RegisterFormModal`,
   `RegisterSessionCell`, `OpenSessionModal`, `CloseSessionModal`.
3. **POS shell + register picker + session gate + catalog** — `PosPage`, `PosShell`,
   `PosTopBar`, `RegisterPicker`, `PosSessionGate`, `PosSearchBar`, `PosProductGrid`.
4. **Cart + discounts + preview totals** — `usePosCart`, `CartList`, `CartLineRow`,
   `CartSummary`, `LineDiscountPopover`, `saleMath` wiring.
5. **Barcode** — `useBarcodeScanner`, wired into `PosTerminal` + search box.
6. **Payment / checkout / idempotency** — `PaymentModal`, checkout mutation, `clientRequestId`
   lifecycle, `PosCompletePage`.
7. **Success / receipt** — `ReceiptPage` + print CSS; wire from complete screen + sale detail.
8. **Sales** — `SalesPage`, `SaleDetailPage`, `SaleItemsTable`, `SaleReturnsList`.
9. **Returns** — `ReturnModal`.
10. **Verification** — `dotnet test Negosio.sln` (kill stale `Negosio.Api` first), `npm run
    lint` + `npm run build`, full headless-Chrome walkthrough: register → open session →
    search + scan → cart + discount → pay (cash change) → success → print receipt → sales list
    → sale detail → return (partial) → inventory reflects restock; plus idempotency
    (double-submit, network-fail retry) and `INSUFFICIENT_INVENTORY`.

---

## 14. Non-goals / deferred

- Split tender, order-level discounts, receipt PDF/email, hardware config, offline queue,
  multi-branch register management UX (works but browser-unverified — one branch per tenant
  today), staff/cashier assignment, dashboard sales metrics, tax settings write UI.
