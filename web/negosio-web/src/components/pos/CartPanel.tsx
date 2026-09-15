import type { ReactNode } from 'react'
import { ArrowRight, CreditCard, ShoppingCart } from 'lucide-react'
import type { DiscountType, TaxSettingsDto } from '../../api/types'
import type { CartLine } from '../../lib/posStorage'
import { formatMoney } from '../../lib/format'
import { Badge, Button, Callout } from '../ui'
import { CartItem } from './CartItem'

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
}: Props) {
  return (
    <div className="flex h-full min-h-0 flex-col rounded-2xl border border-border bg-surface shadow-card">
      {/* The active cart never has a SaleNumber — one is only assigned once checkout succeeds — so
          this never shows a number, guessed or otherwise. It's a fixed label, not derived from
          cart contents: it reads "New sale" whether the cart is empty or full of items. */}
      <div className="flex shrink-0 items-start justify-between gap-2 border-b border-border px-4 py-3">
        <div>
          <p className="text-[11px] font-semibold uppercase tracking-wide text-text-muted">
            Current transaction
          </p>
          <p className="text-sm font-semibold text-text-primary">
            New sale <span className="font-normal text-text-muted">— number assigned at checkout</span>
          </p>
        </div>
        {lines.length > 0 && (
          <Badge tone="blue" className="shrink-0">
            {lines.length} item{lines.length === 1 ? '' : 's'}
          </Badge>
        )}
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {resuming && (
          <div className="px-4 pt-3">
            <Callout tone="info">Resuming an unfinished sale.</Callout>
          </div>
        )}
        {lines.length === 0 ? (
          <div className="flex h-full flex-col items-center justify-center gap-3 px-6 py-10 text-center">
            <div className="grid size-14 place-items-center rounded-full bg-surface-subtle">
              <ShoppingCart className="size-6 text-text-muted" aria-hidden="true" />
            </div>
            <div>
              <p className="text-sm font-semibold text-text-primary">Your cart is empty</p>
              <p className="mt-1 text-[13px] text-text-muted">
                Scan or select a product to start a transaction.
              </p>
            </div>
          </div>
        ) : (
          lines.map((line) => (
            <CartItem
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

      <div className="shrink-0 space-y-3 border-t border-border bg-surface-subtle/60 px-4 py-4">
        {notice}
        <dl className="space-y-1.5 text-sm">
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
          <div className="flex items-center justify-between border-t border-border pt-2.5">
            <dt className="text-base font-bold text-text-primary">Total</dt>
            <dd className="text-2xl font-extrabold tabular-nums text-text-primary">
              {taxPending ? 'Calculating…' : formatMoney(totals.grandTotal)}
            </dd>
          </div>
        </dl>
        <Button
          block
          size="lg"
          disabled={chargeDisabled || lines.length === 0}
          onClick={onCharge}
          leadingIcon={<CreditCard className="size-5" aria-hidden="true" />}
          trailingIcon={<ArrowRight className="size-4" aria-hidden="true" />}
        >
          Charge {formatMoney(totals.grandTotal)}
        </Button>
      </div>
    </div>
  )
}
