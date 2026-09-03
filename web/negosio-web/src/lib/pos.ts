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
 * Quick-cash suggestions for a cash payment: the exact amount, then the next round PHP note
 * above it (50 / 100 / 500 / 1000 boundaries), deduped, ascending, max 4.
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
