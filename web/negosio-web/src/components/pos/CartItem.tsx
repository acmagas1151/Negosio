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
  onSetDeliveryRequired: (variantId: string, quantity: number) => void
  onSetPickupRequired: (variantId: string, quantity: number) => void
}

function discountLabel(d: { type: DiscountType; value: number }): string {
  if (d.type === 'Percentage') return `${formatQty(d.value)}% off`
  if (d.type === 'FixedAmount') return `${formatMoney(d.value)} off`
  return 'Discount'
}

export function CartItem({
  line,
  tax,
  taxPending,
  onSetQty,
  onRemove,
  onSetDiscount,
  onSetDeliveryRequired,
  onSetPickupRequired,
}: Props) {
  const [discountOpen, setDiscountOpen] = useState(false)
  // While the qty field is being edited it holds a raw string; `null` means "show the committed
  // quantity". This lets the cashier clear the field and retype without the line vanishing.
  const [qtyDraft, setQtyDraft] = useState<string | null>(null)
  // Same draft-until-blur pattern as qtyDraft, one independent instance per allocation input so
  // editing one never disturbs the other's in-progress draft.
  const [deliveryDraft, setDeliveryDraft] = useState<string | null>(null)
  const [pickupDraft, setPickupDraft] = useState<string | null>(null)

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

  // Clamp direction: on commit, cap the value against what the OTHER allocation currently holds —
  // never reduce the other one to make room, since the user didn't touch it. usePosCart's reducer
  // re-applies the same cap defensively, but computing it here too means the input reflects the
  // clamped value immediately rather than round-tripping through a rejected-then-corrected state.
  const commitDelivery = (raw: string) => {
    setDeliveryDraft(null)
    const trimmed = raw.trim()
    const n = Number(trimmed)
    if (trimmed !== '' && Number.isFinite(n)) {
      const max = Math.max(0, line.quantity - line.pickupRequiredQuantity)
      onSetDeliveryRequired(line.variantId, Math.max(0, Math.min(n, max)))
    }
  }
  const commitPickup = (raw: string) => {
    setPickupDraft(null)
    const trimmed = raw.trim()
    const n = Number(trimmed)
    if (trimmed !== '' && Number.isFinite(n)) {
      const max = Math.max(0, line.quantity - line.deliveryRequiredQuantity)
      onSetPickupRequired(line.variantId, Math.max(0, Math.min(n, max)))
    }
  }

  const takeNow = Math.max(0, line.quantity - line.deliveryRequiredQuantity - line.pickupRequiredQuantity)

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

        {/* Sold = TakeNow + Delivery + Pickup, always. Take-now is derived and read-only — making
            all three editable would create a three-way constraint with no clear resolution when
            the cashier edits one. Delivery/pickup each clamp on commit against the OTHER
            allocation's current value (never against a stale snapshot), so the identity always
            holds without ever silently reducing the input the cashier didn't touch. */}
        <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1.5 text-[12px]">
          <span className="text-text-muted">
            Take now <span className="font-semibold text-text-secondary">{formatQty(takeNow)}</span>
          </span>
          <label className="flex items-center gap-1.5 text-text-secondary">
            Delivery
            <input
              type="number"
              min={0}
              step="0.001"
              value={deliveryDraft ?? String(line.deliveryRequiredQuantity)}
              onChange={(e) => setDeliveryDraft(e.target.value)}
              onBlur={(e) => commitDelivery(e.currentTarget.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  e.currentTarget.blur()
                }
              }}
              aria-label={`Delivery quantity for ${line.name}`}
              className="h-7 w-14 rounded-md border border-border-strong bg-white px-1.5 text-center text-[12px] text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[2px] focus:ring-primary-500/15"
            />
          </label>
          <label className="flex items-center gap-1.5 text-text-secondary">
            Pickup
            <input
              type="number"
              min={0}
              step="0.001"
              value={pickupDraft ?? String(line.pickupRequiredQuantity)}
              onChange={(e) => setPickupDraft(e.target.value)}
              onBlur={(e) => commitPickup(e.currentTarget.value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') {
                  e.preventDefault()
                  e.currentTarget.blur()
                }
              }}
              aria-label={`Pickup quantity for ${line.name}`}
              className="h-7 w-14 rounded-md border border-border-strong bg-white px-1.5 text-center text-[12px] text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[2px] focus:ring-primary-500/15"
            />
          </label>
        </div>
      </div>
    </div>
  )
}
