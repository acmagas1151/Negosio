import { useState } from 'react'
import { Minus, Package, Plus, X } from 'lucide-react'
import type { DiscountType, TaxSettingsDto } from '../../api/types'
import type { CartLine } from '../../lib/posStorage'
import { calcLine } from '../../lib/saleMath'
import { formatMoney, formatQty } from '../../lib/format'
import { cn } from '../../lib/cn'
import { LineDiscountPopover } from './LineDiscountPopover'

interface Props {
  line: CartLine
  tax: TaxSettingsDto | undefined
  taxPending: boolean
  onSetQty: (variantId: string, qty: number) => void
  onRemove: (variantId: string) => void
  onSetDiscount: (variantId: string, d: { type: DiscountType; value: number }) => void
}

function discountLabel(d: { type: DiscountType; value: number }): string {
  if (d.type === 'Percentage') return `${formatQty(d.value)}% off`
  if (d.type === 'FixedAmount') return `${formatMoney(d.value)} off`
  return 'Discount'
}

export function CartItem({ line, tax, taxPending, onSetQty, onRemove, onSetDiscount }: Props) {
  const [discountOpen, setDiscountOpen] = useState(false)
  // While the qty field is being edited it holds a raw string; `null` means "show the committed
  // quantity". This lets the cashier clear the field and retype without the line vanishing.
  const [qtyDraft, setQtyDraft] = useState<string | null>(null)

  // Commit from the field's own value (not React state) so a programmatic set-then-blur or a
  // paste-then-tab still applies — the committed number is always what the input actually holds.
  const commitQty = (raw: string) => {
    setQtyDraft(null)
    const trimmed = raw.trim()
    const n = Number(trimmed)
    if (trimmed !== '' && Number.isFinite(n)) {
      onSetQty(line.variantId, n)
    }
  }

  const stepQty = (next: number) => {
    setQtyDraft(null)
    onSetQty(line.variantId, next)
  }

  const amounts = tax
    ? calcLine({
        unitPrice: line.unitPrice,
        quantity: line.quantity,
        discount: line.discount,
        taxRatePercent: tax.taxRatePercent,
        pricesIncludeTax: tax.pricesIncludeTax,
      })
    : null
  const lineValue = amounts ? amounts.net : line.unitPrice * line.quantity

  return (
    <div className="flex gap-3 border-b border-border-light px-4 py-3">
      <div className="grid size-11 shrink-0 place-items-center rounded-lg bg-primary-50">
        <Package className="size-5 text-primary-300" aria-hidden="true" />
      </div>

      <div className="min-w-0 flex-1">
        <div className="flex items-start justify-between gap-2">
          <div className="min-w-0">
            <p className="truncate text-sm font-semibold text-text-primary">{line.name}</p>
            {line.variantName && (
              <p className="truncate text-[12px] text-text-muted">{line.variantName}</p>
            )}
            <p className="text-[12px] text-text-muted">{formatMoney(line.unitPrice)} each</p>
          </div>
          <button
            type="button"
            onClick={() => onRemove(line.variantId)}
            aria-label={`Remove ${line.name}`}
            className="shrink-0 rounded p-1 text-text-muted hover:bg-surface-subtle hover:text-danger"
          >
            <X className="size-4" aria-hidden="true" />
          </button>
        </div>

        <div className="mt-2 flex items-center justify-between gap-2">
          <div className="inline-flex items-center rounded-lg border border-border-strong">
            <button
              type="button"
              aria-label="Decrease quantity"
              onClick={() => stepQty(line.quantity - 1)}
              className="grid size-7 place-items-center text-text-secondary hover:bg-surface-subtle"
            >
              <Minus className="size-3.5" aria-hidden="true" />
            </button>
            <input
              type="number"
              min={0}
              step="0.001"
              value={qtyDraft ?? String(line.quantity)}
              onChange={(e) => setQtyDraft(e.target.value)}
              onBlur={(e) => commitQty(e.currentTarget.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  e.currentTarget.blur()
                }
              }}
              aria-label={`Quantity for ${line.name}`}
              className="h-7 w-12 border-x border-border-strong text-center text-sm text-text-primary focus:outline-none"
            />
            <button
              type="button"
              aria-label="Increase quantity"
              onClick={() => stepQty(line.quantity + 1)}
              className="grid size-7 place-items-center text-text-secondary hover:bg-surface-subtle"
            >
              <Plus className="size-3.5" aria-hidden="true" />
            </button>
          </div>

          <div className="relative">
            <button
              type="button"
              onClick={() => setDiscountOpen((v) => !v)}
              className={cn(
                'rounded-lg px-2 py-1 text-[12px] font-semibold',
                line.discount.type !== 'None'
                  ? 'bg-primary-50 text-primary-700'
                  : 'text-text-muted hover:bg-surface-subtle',
              )}
            >
              {discountLabel(line.discount)}
            </button>
            {discountOpen && (
              <LineDiscountPopover
                discount={line.discount}
                onChange={(d) => onSetDiscount(line.variantId, d)}
                onClose={() => setDiscountOpen(false)}
              />
            )}
          </div>

          <span className="min-w-[4.5rem] text-right text-sm font-bold text-text-primary">
            {taxPending ? '…' : formatMoney(lineValue)}
          </span>
        </div>
      </div>
    </div>
  )
}
