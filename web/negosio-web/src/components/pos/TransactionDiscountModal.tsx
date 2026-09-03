import { useEffect, useState } from 'react'
import type { CartLine } from '../../lib/posStorage'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal } from '../ui'
import { inputClass } from '../ui/TextField'
import { FormField } from '../ui/FormField'

interface Props {
  open: boolean
  onClose: () => void
  lines: CartLine[]
  onApply: (percent: number) => void
}

/**
 * v1 whole-cart discount: percentage only, applied by mirroring the same percentage onto every
 * cart line's existing (backend-authoritative) discount field. This is exact — X% off N line
 * totals equals X% off their sum — so the resulting checkout total is real, server-computed math,
 * not a client-only estimate. Refuses to apply over lines that already carry their own discount
 * rather than silently overwriting them; the cashier removes those first or discounts per line.
 *
 * A fixed peso-amount transaction discount is intentionally NOT offered here: fairly prorating a
 * flat amount across lines would need its own backend field to stay honestly "order-level" (see
 * docs/superpowers/specs/2026-09-03-pos-redesign — Discounts gap). Per-line discounting (the
 * existing popover in the cart) still supports a fixed amount today.
 */
export function TransactionDiscountModal({ open, onClose, lines, onApply }: Props) {
  const [value, setValue] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setValue('')
  }, [open])

  const linesWithDiscount = lines.filter((l) => l.discount.type !== 'None')
  const blocked = linesWithDiscount.length > 0
  const percent = Math.min(100, Math.max(0, Number(value) || 0))
  const canApply = !blocked && value.trim() !== '' && percent > 0

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Apply discount"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button
            size="sm"
            disabled={!canApply}
            onClick={() => {
              onApply(percent)
              onClose()
            }}
          >
            Apply discount
          </Button>
        </>
      }
    >
      {blocked ? (
        <Callout tone="warning">
          {linesWithDiscount.length} item{linesWithDiscount.length === 1 ? '' : 's'} in this cart
          already {linesWithDiscount.length === 1 ? 'has' : 'have'} its own discount. Remove those
          line discounts first, or discount that item directly from the cart instead — applying a
          whole-cart discount here would never overwrite them.
        </Callout>
      ) : (
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
          <p className="text-[12px] text-text-muted">
            Applies to every item in the cart. Fixed peso-amount whole-cart discounts aren&rsquo;t
            available yet — use the per-item discount for that.
          </p>
        </>
      )}
    </Modal>
  )
}
