import type {
  CancellationDisposition,
  FulfillmentMethod,
  FulfillmentStatus,
  PaymentMethod,
  SaleFulfillmentStatus,
  SaleStatus,
} from '../api/types'

export const PAYMENT_METHOD_LABELS: Record<PaymentMethod, string> = {
  Cash: 'Cash',
  Card: 'Card',
  GCash: 'GCash',
  Maya: 'Maya',
  // Stored value stays BankTransfer — only the display label is InstaPay, consistent everywhere
  // (POS, receipts, Sales, Returns, Reports) rather than special-casing any one page.
  BankTransfer: 'InstaPay',
  Other: 'Other',
}

/** Methods offered in the POS payment modal, in display order. */
export const POS_PAYMENT_METHODS: PaymentMethod[] = ['Cash', 'Card', 'GCash', 'Maya', 'BankTransfer']

// Card carries an approval code rather than a plain reference; every other non-cash method just
// gets the generic label. Shared by the payment modal and the success confirmation so the two
// never drift into two different names for the same field. Cash has no reference field at all.
export const REFERENCE_LABELS: Partial<Record<PaymentMethod, string>> = {
  Card: 'Reference / approval code',
}

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

/** The spec's label table. A status never renders on its own — it always renders through its
 * method, because "Completed" means "Delivered" for a delivery and "Claimed" for a pickup. */
export function fulfillmentStatusLabel(method: FulfillmentMethod, status: FulfillmentStatus): string {
  if (method === 'TakeNow') return status === 'Completed' ? 'Taken now' : 'Take now'
  if (method === 'Delivery') {
    switch (status) {
      case 'Unscheduled': return 'Deliver later'
      case 'Pending': return 'Pending delivery'
      case 'Completed': return 'Delivered'
      case 'Cancelled': return 'Cancelled delivery'
    }
  }
  switch (status) {
    case 'Unscheduled': return 'Pickup not scheduled'
    case 'Pending': return 'Pending pickup'
    case 'Completed': return 'Claimed'
    case 'Cancelled': return 'Cancelled pickup'
  }
}

export function fulfillmentStatusTone(status: FulfillmentStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (status) {
    case 'Completed': return 'success'
    case 'Pending': return 'warning'
    case 'Cancelled': return 'danger'
    default: return 'neutral'
  }
}

export const FULFILLMENT_METHOD_LABELS: Record<FulfillmentMethod, string> = {
  TakeNow: 'Take now',
  Delivery: 'Delivery',
  Pickup: 'Pickup',
}

export const CANCELLATION_DISPOSITION_LABELS: Record<CancellationDisposition, string> = {
  DeliverLater: 'Deliver later (reschedule)',
  PickupLater: 'Pick up later (reschedule)',
  ConvertToDelivery: 'Convert to delivery',
  ConvertToPickup: 'Convert to pickup',
  CustomerPickedUpInstead: 'Customer picked it up instead',
}

export const SALE_FULFILLMENT_STATUS_LABELS: Record<SaleFulfillmentStatus, string> = {
  NotApplicable: 'No fulfillment needed',
  Fulfilled: 'Fulfilled',
  PartiallyFulfilled: 'Partially fulfilled',
  AwaitingDelivery: 'Awaiting delivery',
  AwaitingPickup: 'Awaiting pickup',
  AwaitingDeliveryAndPickup: 'Awaiting delivery and pickup',
  NeedsScheduling: 'Needs scheduling',
  NeedsAttention: 'Needs attention',
}

export function saleFulfillmentStatusTone(s: SaleFulfillmentStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (s) {
    case 'Fulfilled': return 'success'
    case 'NeedsAttention': return 'danger'
    case 'NotApplicable': return 'neutral'
    default: return 'warning'
  }
}

/**
 * Friendly explanations for `voidIneligibilityCode` / a void attempt's rejection code. Shared by
 * SaleDetailPage (a muted hint next to where the Void button would have been) and VoidSaleModal
 * (the error shown when a void attempt is rejected mid-flow) so both read from one map instead of
 * two copies that can drift.
 */
export const VOID_INELIGIBLE_MESSAGES: Record<string, string> = {
  SALE_NOT_VOIDABLE: 'This sale cannot be voided.',
  SALE_HAS_RETURNS: 'This sale has returns against it and cannot be voided.',
  VOID_SESSION_CLOSED:
    'This sale can no longer be voided because its register session has already been closed. Use the return/refund process instead.',
  VOID_CUTOFF_EXPIRED: 'This sale is past the void cutoff. Use the return/refund process instead.',
}

/**
 * The most recently completed sale in this register session — just enough to label it and look it
 * up. This is deliberately NOT "the active transaction": the cart being worked on right now has no
 * SaleNumber until checkout succeeds, and this reference must never be presented as if it were that
 * cart. It's sourced from the freshest backend read (the register session's most recent sale,
 * status included), so it reflects a void from anywhere — this terminal, another tab, the Sales
 * page — not just one made through this component. Never a placeholder — always a real,
 * backend-issued sale, or null if this session has no sale yet.
 */
export interface LastSaleRef {
  saleId: string
  saleNumber: string
  status: SaleStatus
}

export interface FulfillmentScheduleItemAllocation {
  variantId: string
  quantity: number
}

/** One delivery-or-pickup schedule being built in the payment modal. `key` is a local
 * React/list-diffing id only — never sent to the backend. Field names mirror the backend's
 * `CreateDeliveryReceiptRequest` / `CreatePickupRequest` (`scheduledDate`, `notes`) so building the
 * request body at submit time is a near-direct mapping. `deliveryAddress` is simply unused (kept
 * empty) for a Pickup schedule — Pickup has no address field at all; see FulfillmentDetailsFields,
 * which never renders it for that method. Maps to a real request once the sale exists and
 * `variantId`s can be resolved to real `saleItemId`s (see PosTerminal's batch-submit mutations). */
export interface FulfillmentSchedule {
  key: string
  scheduledDate: string // yyyy-MM-dd, browser-local — see the plan's Global Constraints
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  notes: string
  items: FulfillmentScheduleItemAllocation[]
}

/** Browser-local "today" as a `yyyy-MM-dd` date-input value. This repo has no tenant-timezone model
 * on the frontend yet (see the plan's Global Constraints) — this is a UI default only; the backend
 * independently rejects a past date against its own business-local clock regardless of what this
 * produces. */
export function todayLocalDateInput(): string {
  const d = new Date()
  const yyyy = d.getFullYear()
  const mm = String(d.getMonth() + 1).padStart(2, '0')
  const dd = String(d.getDate()).padStart(2, '0')
  return `${yyyy}-${mm}-${dd}`
}

export function emptyFulfillmentSchedule(): FulfillmentSchedule {
  return {
    key: crypto.randomUUID(),
    scheduledDate: todayLocalDateInput(),
    recipientName: '',
    deliveryAddress: '',
    contactNumber: '',
    notes: '',
    items: [],
  }
}

/** Sum of quantity a variant already has allocated across every schedule except `excludeKey` (pass
 * the schedule currently being edited so its own not-yet-committed value doesn't count against
 * itself). Used only to compute a helpful UI cap — the backend re-validates independently and is
 * the only source of truth for what's actually available (see the plan's Global Constraints). */
export function scheduledQuantityFor(
  schedules: FulfillmentSchedule[],
  variantId: string,
  excludeKey?: string,
): number {
  return schedules
    .filter((s) => s.key !== excludeKey)
    .reduce((sum, s) => sum + (s.items.find((i) => i.variantId === variantId)?.quantity ?? 0), 0)
}

/**
 * Reconciles a method's schedules against that method's CURRENT per-variant required quantity
 * (e.g. `{ [variantId]: cartLine.deliveryRequiredQuantity }`), spending a shared per-variant budget
 * across schedules in encounter order. A variant no longer in `requiredByVariant` (line removed, or
 * its requirement dropped to 0) has its schedule items dropped entirely; a variant whose required
 * amount shrank has its schedule items clamped down to fit.
 *
 * This is the fix carried forward from the delivery-only implementation: a cart line edited or
 * removed after its schedule was drafted must never leave a schedule holding a stale
 * `variantId`/quantity pair, because posting one fails the WHOLE batch for a sale that has already
 * been paid for. Called both reactively (so the UI never shows a schedule that's gone stale while
 * the cashier is still building the cart) and again, defensively, immediately before each
 * post-checkout batch post (see PosTerminal's batch mutations) — the two calls guard the same
 * invariant at different points in time, so a race between them is harmless.
 */
export function reconcileSchedules(
  schedules: FulfillmentSchedule[],
  requiredByVariant: Record<string, number>,
): { schedules: FulfillmentSchedule[]; changed: boolean } {
  const remaining = { ...requiredByVariant }
  let changed = false
  const next = schedules.map((s) => {
    const items: FulfillmentScheduleItemAllocation[] = []
    for (const item of s.items) {
      const budget = remaining[item.variantId] ?? 0
      if (budget <= 0) {
        changed = true
        continue
      }
      if (item.quantity <= budget) {
        remaining[item.variantId] = budget - item.quantity
        items.push(item)
      } else {
        changed = true
        remaining[item.variantId] = 0
        items.push({ ...item, quantity: budget })
      }
    }
    if (items.length !== s.items.length) changed = true
    return items.length === s.items.length && items.every((i, idx) => i === s.items[idx])
      ? s
      : { ...s, items }
  })
  return { schedules: changed ? next : schedules, changed }
}

/** Default/reset value for the delivery-charge input — a formatted string so the field always
 * starts showing "0.00", matching how a cashier would type a peso amount. */
export const EMPTY_DELIVERY_CHARGE = '0.00'

const DELIVERY_CHARGE_PATTERN = /^\d+(\.\d{1,2})?$/

/** True only for a non-negative amount with at most 2 decimal places — the same rule the backend
 * enforces in CheckoutRequestValidator. Checked against the raw string (not a parsed float) so a
 * value like "10.005" is rejected exactly, with no floating-point rounding ambiguity. */
export function isValidDeliveryChargeInput(raw: string): boolean {
  return DELIVERY_CHARGE_PATTERN.test(raw.trim())
}

/** The delivery charge to actually use for live total/change math: 0 whenever delivery isn't
 * selected or the typed value isn't a valid non-negative number yet (mid-typing), so the running
 * total never shows NaN or a negative figure — Confirm payment is separately blocked until the
 * value passes isValidDeliveryChargeInput. */
export function parseDeliveryCharge(forDelivery: boolean, raw: string): number {
  if (!forDelivery) return 0
  const n = Number(raw)
  return Number.isFinite(n) && n > 0 ? n : 0
}

/**
 * Quick-cash suggestions for a cash payment: the exact amount, then the next round PHP note
 * above it (50 / 100 / 500 / 1000 boundaries), deduped, ascending. Every step has its own
 * candidate, so the cap must cover all of them — one per step plus the exact amount.
 */
export function suggestCashButtons(total: number): number[] {
  if (!(total > 0)) return []
  const steps = [50, 100, 500, 1000]
  const out = new Set<number>()
  out.add(Math.ceil(total * 100) / 100)
  for (const step of steps) {
    out.add(Math.ceil(total / step) * step)
  }
  return [...out].sort((a, b) => a - b).slice(0, steps.length + 1)
}
