import { useState } from 'react'
import type { ReactNode } from 'react'
import { Minus, Plus, X } from 'lucide-react'
import type { DiscountType, TaxSettingsDto } from '../../api/types'
import type { CartLine } from '../../lib/posStorage'
import type { CurrentSaleRef } from '../../lib/pos'
import { calcLine } from '../../lib/saleMath'
import { formatMoney, formatQty } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout } from '../ui'
import { LineDiscountPopover } from './LineDiscountPopover'

interface Props {
  lines: CartLine[]
  totals: { subtotal: number; discountTotal: number; taxTotal: number; grandTotal: number }
  tax: TaxSettingsDto | undefined
  taxPending: boolean
  onSetQty: (variantId: string, qty: number) => void
  onRemove: (variantId: string) => void
  onSetDiscount: (variantId: string, d: { type: DiscountType; value: number }) => void
  onCharge: () => void
  chargeDisabled: boolean
  notice?: ReactNode
  resuming?: boolean
  /** The terminal's current sale, shown large at the top of the cart column — a completed sale,
   * unrelated to whatever is in the cart below it (voiding it never touches these lines). */
  currentSale?: CurrentSaleRef | null
}

function discountLabel(d: { type: DiscountType; value: number }): string {
  if (d.type === 'Percentage') return `${formatQty(d.value)}% off`
  if (d.type === 'FixedAmount') return `${formatMoney(d.value)} off`
  return 'Discount'
}

function CartLineRow({
  line,
  tax,
  taxPending,
  onSetQty,
  onRemove,
  onSetDiscount,
}: {
  line: CartLine
  tax: TaxSettingsDto | undefined
  taxPending: boolean
  onSetQty: (variantId: string, qty: number) => void
  onRemove: (variantId: string) => void
  onSetDiscount: (variantId: string, d: { type: DiscountType; value: number }) => void
}) {
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
    <div className="border-b border-border px-4 py-3">
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
          className="rounded p-1 text-text-muted hover:bg-surface-subtle hover:text-danger"
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
            className="grid size-8 place-items-center text-text-secondary hover:bg-surface-subtle"
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
            className="h-8 w-14 border-x border-border-strong text-center text-sm text-text-primary focus:outline-none"
          />
          <button
            type="button"
            aria-label="Increase quantity"
            onClick={() => stepQty(line.quantity + 1)}
            className="grid size-8 place-items-center text-text-secondary hover:bg-surface-subtle"
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

        <span className="min-w-[4.5rem] text-right text-sm font-semibold text-text-primary">
          {taxPending ? '…' : formatMoney(lineValue)}
        </span>
      </div>
    </div>
  )
}

export function CartPanel({
  lines,
  totals,
  tax,
  taxPending,
  onSetQty,
  onRemove,
  onSetDiscount,
  onCharge,
  chargeDisabled,
  notice,
  resuming,
  currentSale,
}: Props) {
  return (
    <div className="flex h-full min-h-0 flex-col border-l border-border bg-surface">
      {currentSale && (
        <div className="shrink-0 border-b border-border bg-primary-50 px-4 py-3">
          <p className="text-[11px] font-semibold uppercase tracking-wide text-primary-700">
            Current sale
          </p>
          <p className="font-mono text-2xl font-extrabold tabular-nums text-primary-900">
            #{currentSale.saleNumber}
          </p>
        </div>
      )}

      <div className="shrink-0 border-b border-border px-4 py-3">
        <h2 className="text-sm font-bold uppercase tracking-wide text-text-muted">Cart</h2>
        <p className="text-[12px] text-text-muted">
          {lines.length} item{lines.length === 1 ? '' : 's'}
        </p>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {resuming && (
          <div className="px-4 pt-3">
            <Callout tone="info">Resuming an unfinished sale.</Callout>
          </div>
        )}
        {lines.length === 0 ? (
          <div className="px-4 py-10 text-center">
            <p className="text-sm font-semibold text-text-primary">Your cart is empty</p>
            <p className="mt-1 text-[13px] text-text-muted">
              Scan or select a product to start a transaction.
            </p>
          </div>
        ) : (
          lines.map((line) => (
            <CartLineRow
              key={line.variantId}
              line={line}
              tax={tax}
              taxPending={taxPending}
              onSetQty={onSetQty}
              onRemove={onRemove}
              onSetDiscount={onSetDiscount}
            />
          ))
        )}
      </div>

      <div className="shrink-0 space-y-3 border-t border-border px-4 py-4">
        {notice}
        <dl className="space-y-1 text-sm">
          <div className="flex justify-between text-text-secondary">
            <dt>Subtotal</dt>
            <dd>{taxPending ? '…' : formatMoney(totals.subtotal)}</dd>
          </div>
          {totals.discountTotal > 0 && (
            <div className="flex justify-between text-text-secondary">
              <dt>Discount</dt>
              <dd>−{formatMoney(totals.discountTotal)}</dd>
            </div>
          )}
          <div className="flex justify-between text-text-secondary">
            <dt>
              Tax <span className="text-text-muted">(estimated)</span>
            </dt>
            <dd>{taxPending ? '…' : formatMoney(totals.taxTotal)}</dd>
          </div>
          <div className="flex justify-between pt-1 text-base font-bold text-text-primary">
            <dt>Total</dt>
            <dd>{taxPending ? 'Calculating…' : formatMoney(totals.grandTotal)}</dd>
          </div>
        </dl>
        <Button
          block
          size="md"
          disabled={chargeDisabled || lines.length === 0}
          onClick={onCharge}
        >
          Charge {formatMoney(totals.grandTotal)}
        </Button>
      </div>
    </div>
  )
}
