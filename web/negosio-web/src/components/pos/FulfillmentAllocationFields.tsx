import type { FulfillmentSchedule } from '../../lib/pos'
import { computeFulfillmentAllocationSummary } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { QuantityInput } from './FulfillmentDetailsFields'

export interface AllocationCartLine {
  variantId: string
  name: string
  variantName: string | null
  quantity: number
  deliveryRequiredQuantity: number
  pickupRequiredQuantity: number
}

interface Props {
  cartLines: AllocationCartLine[]
  deliverySchedules: FulfillmentSchedule[]
  pickupSchedules: FulfillmentSchedule[]
  onSetDeliveryRequired: (variantId: string, quantity: number) => void
  onSetPickupRequired: (variantId: string, quantity: number) => void
  disabled?: boolean
}

const ROW_GRID =
  'grid grid-cols-2 gap-x-3 gap-y-1 sm:grid-cols-[minmax(0,1fr)_3.5rem_4.5rem_4.75rem_4.75rem] sm:items-center sm:gap-2'

const INPUT_CLASS =
  'h-8 w-full rounded-md border border-border-strong bg-white px-2 text-right text-[13px] text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[2px] focus:ring-primary-500/15'

function SummaryStat({ label, value }: { label: string; value: number }) {
  return (
    <div>
      <dt className="text-[11px] text-text-muted">{label}</dt>
      <dd className="text-sm font-semibold text-text-primary">{formatQty(value)}</dd>
    </div>
  )
}

/**
 * The Take now / Delivery / Pickup allocation table that used to live on each `CartItem` cart
 * line, now relocated into the Payment modal. Editing a row here calls straight through to
 * `onSetDeliveryRequired`/`onSetPickupRequired` — the same `PosTerminal` callbacks CartItem used
 * to call — so the underlying state (`usePosCart`'s reducer, persisted via `posStorage`) and its
 * clamping/idempotency-rotation behavior are entirely unchanged; only which component edits it
 * moved.
 */
export function FulfillmentAllocationFields({
  cartLines,
  deliverySchedules,
  pickupSchedules,
  onSetDeliveryRequired,
  onSetPickupRequired,
  disabled,
}: Props) {
  const summary = computeFulfillmentAllocationSummary(cartLines, deliverySchedules, pickupSchedules)
  const anyDelivery = cartLines.some((l) => l.deliveryRequiredQuantity > 0)
  const anyPickup = cartLines.some((l) => l.pickupRequiredQuantity > 0)

  return (
    <div>
      {/* Column headers — hidden below sm, where each row shows its own inline field labels
          instead (see the per-field labels below), matching the stacked-pair layout at that
          width. */}
      <div className={`${ROW_GRID} hidden pb-1 text-[11px] font-medium uppercase tracking-wide text-text-muted sm:grid`}>
        <span>Product</span>
        <span className="text-right">Qty</span>
        <span className="text-right">Take now</span>
        <span>Delivery</span>
        <span>Pickup</span>
      </div>

      <div className="divide-y divide-border-light">
        {cartLines.map((line) => {
          const takeNow = Math.max(
            0,
            line.quantity - line.deliveryRequiredQuantity - line.pickupRequiredQuantity,
          )
          // Same clamp direction CartItem used to apply on commit: cap the field just touched
          // against what the OTHER allocation currently holds, never reducing the other one to
          // make room. usePosCart's reducer re-applies the same cap defensively.
          const deliveryMax = Math.max(0, line.quantity - line.pickupRequiredQuantity)
          const pickupMax = Math.max(0, line.quantity - line.deliveryRequiredQuantity)
          const label = line.variantName ? `${line.name} · ${line.variantName}` : line.name

          return (
            <div key={line.variantId} className={`${ROW_GRID} py-2 first:pt-0`}>
              <div className="col-span-2 min-w-0 sm:col-span-1">
                <p className="truncate text-[13px] text-text-primary">{label}</p>
              </div>

              <div className="text-[13px] text-text-secondary sm:text-right">
                <span className="text-text-muted sm:hidden">Qty </span>
                {formatQty(line.quantity)}
              </div>

              <div className="text-[13px] text-text-muted sm:text-right">
                <span className="sm:hidden">Take now </span>
                <span className="font-semibold text-text-secondary">{formatQty(takeNow)}</span>
              </div>

              <label className="flex items-center justify-between gap-2 text-[12px] text-text-muted sm:block">
                <span className="sm:sr-only">Delivery</span>
                <QuantityInput
                  value={line.deliveryRequiredQuantity}
                  max={deliveryMax}
                  onCommit={(v) => onSetDeliveryRequired(line.variantId, v)}
                  ariaLabel={`Delivery quantity for ${line.name}`}
                  disabled={disabled}
                  className={INPUT_CLASS}
                />
              </label>

              <label className="flex items-center justify-between gap-2 text-[12px] text-text-muted sm:block">
                <span className="sm:sr-only">Pickup</span>
                <QuantityInput
                  value={line.pickupRequiredQuantity}
                  max={pickupMax}
                  onCommit={(v) => onSetPickupRequired(line.variantId, v)}
                  ariaLabel={`Pickup quantity for ${line.name}`}
                  disabled={disabled}
                  className={INPUT_CLASS}
                />
              </label>
            </div>
          )
        })}
      </div>

      {/* "Scheduled for X" counts quantity already covered by an active schedule
          (scheduledQuantityFor); "X later" is the remainder of what's allocated to that method but
          not yet covered by any schedule — the two are kept separate rather than lumped into one
          "allocated" number, since a schedule can cover only part of a line's requirement. */}
      <dl className="mt-3 grid grid-cols-2 gap-x-4 gap-y-2 rounded-lg bg-surface-subtle px-3 py-2.5 sm:grid-cols-3">
        <SummaryStat label="Total sold" value={summary.totalSold} />
        <SummaryStat label="Take now" value={summary.takeNow} />
        {anyDelivery && <SummaryStat label="Scheduled for delivery" value={summary.scheduledForDelivery} />}
        {anyDelivery && <SummaryStat label="Deliver later" value={summary.deliverLater} />}
        {anyPickup && <SummaryStat label="Scheduled for pickup" value={summary.scheduledForPickup} />}
        {anyPickup && <SummaryStat label="Pickup later" value={summary.pickupLater} />}
      </dl>
    </div>
  )
}
