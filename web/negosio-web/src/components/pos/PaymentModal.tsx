import { useEffect, useState } from 'react'
import type { CheckoutPaymentInput, PaymentMethod } from '../../api/types'
import {
  POS_PAYMENT_METHODS,
  PAYMENT_METHOD_LABELS,
  REFERENCE_LABELS,
  isValidDeliveryChargeInput,
  parseDeliveryCharge,
  suggestCashButtons,
  type FulfillmentSchedule,
} from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField } from '../ui'
import { FulfillmentAllocationFields } from './FulfillmentAllocationFields'
import { FulfillmentDetailsFields } from './FulfillmentDetailsFields'
import { PaymentMethodIcon } from './PaymentMethodIcon'

/** One method's schedule list plus its editing callbacks — bundled so PaymentModal can pass a
 * whole method's worth of props in one prop instead of five, once for Delivery and once for
 * Pickup. */
export interface FulfillmentSectionProps {
  schedules: FulfillmentSchedule[]
  onScheduleFieldChange: (key: string, patch: Partial<Omit<FulfillmentSchedule, 'key' | 'items'>>) => void
  onScheduleItemChange: (key: string, variantId: string, quantity: number) => void
  onAddSchedule: () => void
  onRemoveSchedule: (key: string) => void
}

interface Props {
  open: boolean
  onClose: () => void
  amountDue: number
  submitting: boolean
  error: string | null
  onConfirm: (payment: CheckoutPaymentInput) => void
  cartLines: {
    variantId: string
    name: string
    variantName: string | null
    quantity: number
    deliveryRequiredQuantity: number
    pickupRequiredQuantity: number
  }[]
  delivery: FulfillmentSectionProps
  pickup: FulfillmentSectionProps
  /** The typed delivery-charge string, owned by the parent for the same reason schedules is —
   * it must survive the modal closing and reopening after a failed-payment retry. */
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
  /** Editing callbacks for the Take now / Delivery / Pickup allocation table below — the same
   * `PosTerminal` callbacks CartItem used to call directly before this allocation UI moved here.
   * The underlying state (`usePosCart`'s cart lines) is unchanged; only who calls these moved. */
  onSetDeliveryRequired: (variantId: string, quantity: number) => void
  onSetPickupRequired: (variantId: string, quantity: number) => void
}

export function PaymentModal({
  open,
  onClose,
  amountDue,
  submitting,
  error,
  onConfirm,
  cartLines,
  delivery,
  pickup,
  deliveryCharge,
  onDeliveryChargeChange,
  onSetDeliveryRequired,
  onSetPickupRequired,
}: Props) {
  const [method, setMethod] = useState<PaymentMethod>('Cash')
  const [received, setReceived] = useState('')
  const [reference, setReference] = useState('')
  // Fulfillment errors show only after a blocked confirm attempt, not while the cashier is still typing.
  const [fulfillmentAttempted, setFulfillmentAttempted] = useState(false)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setMethod('Cash')
    setReceived('')
    setReference('')
    setFulfillmentAttempted(false)
  }, [open])

  // Whether this sale involves each method at all — derived straight from the cart lines (set via
  // the Fulfillment allocation table below) rather than a manual toggle, so there's no way for the
  // schedule section's visibility to drift from what's actually on the cart.
  const anyDelivery = cartLines.some((l) => l.deliveryRequiredQuantity > 0)
  const anyPickup = cartLines.some((l) => l.pickupRequiredQuantity > 0)
  const deliveryLines = cartLines
    .filter((l) => l.deliveryRequiredQuantity > 0)
    .map((l) => ({
      variantId: l.variantId,
      name: l.name,
      variantName: l.variantName,
      requiredQuantity: l.deliveryRequiredQuantity,
    }))
  const pickupLines = cartLines
    .filter((l) => l.pickupRequiredQuantity > 0)
    .map((l) => ({
      variantId: l.variantId,
      name: l.name,
      variantName: l.variantName,
      requiredQuantity: l.pickupRequiredQuantity,
    }))

  const receivedNum = Number(received)
  const deliveryChargeValid = !anyDelivery || isValidDeliveryChargeInput(deliveryCharge)
  const effectiveAmountDue = amountDue + parseDeliveryCharge(anyDelivery, deliveryCharge)
  const change = method === 'Cash' ? Math.max(0, receivedNum - effectiveAmountDue) : 0

  const activeDeliverySchedules = delivery.schedules.filter((s) => s.items.some((i) => i.quantity > 0))
  const deliveryComplete =
    !anyDelivery ||
    (deliveryChargeValid &&
      activeDeliverySchedules.every((s) => s.scheduledDate && s.recipientName.trim() && s.deliveryAddress.trim()))
  const activePickupSchedules = pickup.schedules.filter((s) => s.items.some((i) => i.quantity > 0))
  const pickupComplete =
    !anyPickup || activePickupSchedules.every((s) => s.scheduledDate && s.recipientName.trim())
  const fulfillmentComplete = deliveryComplete && pickupComplete
  const deliveryErrors =
    anyDelivery && fulfillmentAttempted
      ? { deliveryCharge: deliveryChargeValid ? undefined : 'Enter a valid amount (0 or more, up to 2 decimal places).' }
      : {}

  const paymentComplete =
    method !== 'Cash' || (received.trim() !== '' && receivedNum >= effectiveAmountDue)
  // The button stays enabled while fulfillment fields are incomplete — clicking it then surfaces
  // the inline errors rather than silently doing nothing. It only hard-disables for an incomplete
  // payment or an in-flight submit.
  const canConfirm = !submitting && paymentComplete

  const confirm = () => {
    if (submitting || !paymentComplete) return
    if (!fulfillmentComplete) {
      setFulfillmentAttempted(true)
      return
    }
    if (method === 'Cash') {
      onConfirm({ method: 'Cash', receivedAmount: receivedNum })
    } else {
      onConfirm({ method, amount: effectiveAmountDue, referenceNumber: reference.trim() || null })
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Take payment"
      size="lg"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={submitting}>
            Cancel
          </Button>
          <Button size="sm" onClick={confirm} loading={submitting} disabled={!canConfirm}>
            Confirm payment
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <div className="-mr-1 max-h-[62vh] overflow-y-auto pr-1">
        <div className="mb-4 rounded-lg bg-surface-subtle px-3 py-3 text-center">
          <p className="text-[12px] uppercase tracking-wide text-text-muted">Amount due</p>
          <p className="text-2xl font-bold text-text-primary">{formatMoney(effectiveAmountDue)}</p>
        </div>

        {/* Allocation happens before payment method: how much of the sale is taken now vs. set
            aside for delivery/pickup determines whether the Delivery/Pickup schedule sections
            below even appear, so the cashier settles it first rather than discovering it after
            already picking how they're being paid. */}
        <div className="mb-4 rounded-lg border border-border-strong px-4 py-3.5">
          <p className="text-sm font-semibold text-text-secondary">Fulfillment</p>
          <div className="mt-3">
            <FulfillmentAllocationFields
              cartLines={cartLines}
              deliverySchedules={delivery.schedules}
              pickupSchedules={pickup.schedules}
              onSetDeliveryRequired={onSetDeliveryRequired}
              onSetPickupRequired={onSetPickupRequired}
              disabled={submitting}
            />
          </div>
        </div>

        <div className="mb-4 grid grid-cols-5 gap-2">
          {POS_PAYMENT_METHODS.map((m) => (
            <button
              key={m}
              type="button"
              aria-pressed={method === m}
              onClick={() => setMethod(m)}
              className={cn(
                'flex flex-col items-center gap-1.5 rounded-xl border px-1 py-3 transition-all',
                'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500',
                method === m
                  ? 'border-primary-500 bg-primary-50 shadow-sm'
                  : 'border-border-strong hover:border-primary-200 hover:bg-surface-subtle',
              )}
            >
              <PaymentMethodIcon
                method={m}
                className={cn('size-6', method === m ? 'text-primary-700' : 'text-text-muted')}
              />
              <span
                className={cn(
                  'text-[12px] font-semibold',
                  method === m ? 'text-primary-700' : 'text-text-secondary',
                )}
              >
                {PAYMENT_METHOD_LABELS[m]}
              </span>
            </button>
          ))}
        </div>

        {method === 'Cash' ? (
          <div className="space-y-3">
            <TextField
              label="Cash received"
              name="received"
              type="number"
              min={0}
              step="0.01"
              value={received}
              onChange={(e) => setReceived(e.target.value)}
              autoFocus
            />
            <div className="flex flex-wrap gap-1.5">
              {suggestCashButtons(effectiveAmountDue).map((amt) => (
                <button
                  key={amt}
                  type="button"
                  onClick={() => setReceived(String(amt))}
                  className="rounded-lg border border-border-strong px-3 py-1 text-sm font-semibold text-text-secondary hover:bg-surface-subtle"
                >
                  {formatMoney(amt)}
                </button>
              ))}
            </div>
            <div className="flex justify-between rounded-lg bg-surface-subtle px-3 py-2 text-sm">
              <span className="text-text-muted">Change</span>
              <span className="font-bold text-text-primary">{formatMoney(change)}</span>
            </div>
          </div>
        ) : (
          <TextField
            label={`${REFERENCE_LABELS[method] ?? 'Reference number'} (optional)`}
            name="reference"
            value={reference}
            onChange={(e) => setReference(e.target.value)}
            autoFocus
          />
        )}

        {/* Whether this sale involves delivery and/or pickup is decided by the Fulfillment
            allocation table above (Take now / Delivery / Pickup, per line) — there's no separate
            toggle here to drift out of sync with it. Each method gets its own section, its own
            schedule list, and its own "attempted" gating, but they share one Confirm-payment gate. */}
        {anyDelivery && (
          <div className="mt-4 rounded-lg border border-border-strong px-4 py-3.5">
            <p className="text-sm font-semibold text-text-secondary">Delivery</p>
            <FulfillmentDetailsFields
              method="Delivery"
              cartLines={deliveryLines}
              deliveryCharge={deliveryCharge}
              onDeliveryChargeChange={onDeliveryChargeChange}
              schedules={delivery.schedules}
              onScheduleFieldChange={delivery.onScheduleFieldChange}
              onScheduleItemChange={delivery.onScheduleItemChange}
              onAddSchedule={delivery.onAddSchedule}
              onRemoveSchedule={delivery.onRemoveSchedule}
              errors={deliveryErrors}
              attempted={fulfillmentAttempted}
              disabled={submitting}
            />
          </div>
        )}
        {anyPickup && (
          <div className="mt-4 rounded-lg border border-border-strong px-4 py-3.5">
            <p className="text-sm font-semibold text-text-secondary">Pickup</p>
            <FulfillmentDetailsFields
              method="Pickup"
              cartLines={pickupLines}
              schedules={pickup.schedules}
              onScheduleFieldChange={pickup.onScheduleFieldChange}
              onScheduleItemChange={pickup.onScheduleItemChange}
              onAddSchedule={pickup.onAddSchedule}
              onRemoveSchedule={pickup.onRemoveSchedule}
              errors={{}}
              attempted={fulfillmentAttempted}
              disabled={submitting}
            />
          </div>
        )}
      </div>
    </Modal>
  )
}
