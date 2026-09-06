import { useEffect, useState } from 'react'
import type { CartLine } from '../../lib/posStorage'
import type { DiscountType } from '../../api/types'
import { roundMoney } from '../../lib/saleMath'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal } from '../ui'
import { inputClass } from '../ui/TextField'
import { FormField } from '../ui/FormField'

export interface LineDiscountAssignment {
  variantId: string
  discount: { type: DiscountType; value: number }
}

interface Props {
  open: boolean
  onClose: () => void
  lines: CartLine[]
  onApply: (assignments: LineDiscountAssignment[]) => void
}

type Mode = 'percentage' | 'fixed'

const lineGross = (l: CartLine) => roundMoney(l.unitPrice * l.quantity)

/**
 * Whole-cart discount, applied by mirroring it onto every cart line's existing (backend-
 * authoritative) discount field — so the resulting checkout total is always real, server-computed
 * math, never a client-only estimate.
 *
 * Percentage: the same rate on every line — exact, since X% off N line totals equals X% off their
 * sum. Fixed amount: prorated across lines by each line's share of the cart subtotal, with the
 * last line absorbing whatever centavo of rounding drift is left, so the per-line amounts always
 * sum to exactly the entered total (never more, never less).
 *
 * Refuses to apply over lines that already carry their own discount rather than silently
 * overwriting them; the cashier clears them all from here first (or removes them per line) before
 * applying a fresh whole-cart discount.
 */
export function TransactionDiscountModal({ open, onClose, lines, onApply }: Props) {
  const [mode, setMode] = useState<Mode>('percentage')
  const [value, setValue] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setMode('percentage')
    setValue('')
  }, [open])

  const linesWithDiscount = lines.filter((l) => l.discount.type !== 'None')
  const blocked = linesWithDiscount.length > 0

  const subtotal = roundMoney(lines.reduce((sum, l) => sum + lineGross(l), 0))
  const percent = Math.min(100, Math.max(0, Number(value) || 0))
  const fixedAmount = Math.max(0, Number(value) || 0)
  const fixedExceedsSubtotal = mode === 'fixed' && fixedAmount > subtotal

  const canApply =
    !blocked &&
    value.trim() !== '' &&
    (mode === 'percentage' ? percent > 0 : fixedAmount > 0 && !fixedExceedsSubtotal)

  const clearAllAssignments = (): LineDiscountAssignment[] =>
    lines.map((l) => ({ variantId: l.variantId, discount: { type: 'None', value: 0 } }))

  const buildAssignments = (): LineDiscountAssignment[] => {
    if (mode === 'percentage') {
      return lines.map((l) => ({ variantId: l.variantId, discount: { type: 'Percentage', value: percent } }))
    }
    // Prorate by each line's share of the subtotal; the last line takes the rounding remainder so
    // the parts always sum to exactly fixedAmount.
    let allocated = 0
    return lines.map((l, i) => {
      const isLast = i === lines.length - 1
      const share = isLast
        ? roundMoney(fixedAmount - allocated)
        : roundMoney(fixedAmount * (lineGross(l) / subtotal))
      allocated = roundMoney(allocated + share)
      return { variantId: l.variantId, discount: { type: 'FixedAmount', value: Math.max(0, share) } }
    })
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Apply discount"
      footer={
        blocked ? (
          <>
            <Button variant="secondary" size="sm" onClick={onClose}>
              Cancel
            </Button>
            <Button
              size="sm"
              variant="destructive"
              onClick={() => {
                onApply(clearAllAssignments())
                onClose()
              }}
            >
              Remove all discounts
            </Button>
          </>
        ) : (
          <>
            <Button variant="secondary" size="sm" onClick={onClose}>
              Cancel
            </Button>
            <Button
              size="sm"
              disabled={!canApply}
              onClick={() => {
                onApply(buildAssignments())
                onClose()
              }}
            >
              Apply discount
            </Button>
          </>
        )
      }
    >
      {blocked ? (
        <Callout tone="warning">
          {linesWithDiscount.length} item{linesWithDiscount.length === 1 ? '' : 's'} in this cart
          already {linesWithDiscount.length === 1 ? 'has' : 'have'} its own discount — applying a
          whole-cart discount here would never overwrite them. Remove all of them below to start
          over, or close this and remove line discounts individually from the cart instead.
        </Callout>
      ) : (
        <>
          <div className="mb-3 flex gap-2">
            <button
              type="button"
              aria-pressed={mode === 'percentage'}
              onClick={() => {
                setMode('percentage')
                setValue('')
              }}
              className={cn(
                'rounded-lg px-3 py-1.5 text-sm font-medium transition-colors',
                mode === 'percentage'
                  ? 'bg-primary-600 text-white'
                  : 'border border-border-strong text-text-secondary hover:bg-surface-subtle',
              )}
            >
              Percentage
            </button>
            <button
              type="button"
              aria-pressed={mode === 'fixed'}
              onClick={() => {
                setMode('fixed')
                setValue('')
              }}
              className={cn(
                'rounded-lg px-3 py-1.5 text-sm font-medium transition-colors',
                mode === 'fixed'
                  ? 'bg-primary-600 text-white'
                  : 'border border-border-strong text-text-secondary hover:bg-surface-subtle',
              )}
            >
              Fixed amount
            </button>
          </div>

          {mode === 'percentage' ? (
            <>
              <FormField htmlFor="txn-discount-value" label="Percentage off entire cart">
                <div className="relative">
                  <input
                    id="txn-discount-value"
                    type="number"
                    min={0}
                    max={100}
                    step="1"
                    value={value}
                    onChange={(e) => setValue(e.target.value)}
                    placeholder="0–100"
                    autoFocus
                    className={cn(inputClass, 'pr-8')}
                  />
                  <span className="pointer-events-none absolute right-3 top-1/2 -translate-y-1/2 text-sm text-text-muted">
                    %
                  </span>
                </div>
              </FormField>
              <p className="text-[12px] text-text-muted">Applies to every item in the cart.</p>
            </>
          ) : (
            <>
              <FormField htmlFor="txn-discount-value" label="Amount off entire cart">
                <div className="relative">
                  <span className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-sm text-text-muted">
                    ₱
                  </span>
                  <input
                    id="txn-discount-value"
                    type="number"
                    min={0}
                    max={subtotal}
                    step="0.01"
                    value={value}
                    onChange={(e) => setValue(e.target.value)}
                    placeholder={`0–${subtotal}`}
                    autoFocus
                    className={cn(inputClass, 'pl-7')}
                  />
                </div>
              </FormField>
              {fixedExceedsSubtotal ? (
                <Callout tone="warning">
                  The cart subtotal is only {formatMoney(subtotal)} — enter an amount at or below
                  that.
                </Callout>
              ) : (
                <p className="text-[12px] text-text-muted">
                  Spread proportionally across every item so the cart total drops by exactly this
                  amount.
                </p>
              )}
            </>
          )}
        </>
      )}
    </Modal>
  )
}
