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
  TakeNow: 'Take now',
  PendingDelivery: 'Pending delivery',
  Delivered: 'Delivered',
  PendingPickup: 'Pending pickup',
  Claimed: 'Claimed',
  CancelledOrReplaced: 'Cancelled / replaced',
}

export function saleFulfillmentStatusTone(s: SaleFulfillmentStatus): 'neutral' | 'success' | 'warning' | 'danger' {
  switch (s) {
    case 'Delivered':
    case 'Claimed':
      return 'success'
    case 'PendingDelivery':
    case 'PendingPickup':
      return 'warning'
    case 'CancelledOrReplaced':
      return 'danger'
    case 'TakeNow':
      return 'neutral'
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

/** The single delivery-or-pickup detail form in the payment modal, now that a sale has at most one
 * schedule (covering every item at full quantity — see the plan's whole-sale fulfillment model).
 * Field names mirror the backend's `CreateDeliveryReceiptRequest` / `CreatePickupRequest`
 * (`scheduledDate`, `notes`) so building the request body at submit time is a near-direct mapping.
 * `deliveryAddress` is simply unused (left empty) when the method is Pickup — Pickup has no address
 * field at all; see FulfillmentDetailsFields, which never renders it for that method. */
export interface FulfillmentDetails {
  scheduledDate: string // yyyy-MM-dd
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  notes: string
}

export function emptyFulfillmentDetails(): FulfillmentDetails {
  return {
    scheduledDate: todayLocalDateInput(),
    recipientName: '',
    deliveryAddress: '',
    contactNumber: '',
    notes: '',
  }
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
