import { useEffect, useState } from 'react'
import type { CheckoutPaymentInput, FulfillmentMethod, PaymentMethod } from '../../api/types'
import {
  POS_PAYMENT_METHODS,
  PAYMENT_METHOD_LABELS,
  REFERENCE_LABELS,
  isValidDeliveryChargeInput,
  parseDeliveryCharge,
  suggestCashButtons,
  type FulfillmentDetails,
} from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField } from '../ui'
import { FulfillmentDetailsFields } from './FulfillmentDetailsFields'
import { PaymentMethodIcon } from './PaymentMethodIcon'

interface Props {
  open: boolean
  onClose: () => void
  amountDue: number
  submitting: boolean
  error: string | null
  onConfirm: (payment: CheckoutPaymentInput) => void
  fulfillmentMethod: FulfillmentMethod
  onFulfillmentMethodChange: (method: FulfillmentMethod) => void
  deliveryFields: FulfillmentDetails
  onDeliveryFieldsChange: (patch: Partial<FulfillmentDetails>) => void
  pickupFields: FulfillmentDetails
  onPickupFieldsChange: (patch: Partial<FulfillmentDetails>) => void
  /** The typed delivery-charge string, owned by the parent for the same reason the schedule
   * fields are — it must survive the modal closing and reopening after a failed-payment retry. */
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
}

export function PaymentModal({
  open,
  onClose,
  amountDue,
  submitting,
  error,
  onConfirm,
  fulfillmentMethod,
  onFulfillmentMethodChange,
  deliveryFields,
  onDeliveryFieldsChange,
  pickupFields,
  onPickupFieldsChange,
  deliveryCharge,
  onDeliveryChargeChange,
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

  const receivedNum = Number(received)
  // deliveryCharge's raw string stays in state untouched regardless of the selected method — only
  // its EFFECT on the running total is gated on `fulfillmentMethod === 'Delivery'` here, so
  // switching away from Delivery and back preserves whatever the cashier had already typed.
  const deliveryChargeValid = fulfillmentMethod !== 'Delivery' || isValidDeliveryChargeInput(deliveryCharge)
  const effectiveAmountDue = amountDue + parseDeliveryCharge(fulfillmentMethod === 'Delivery', deliveryCharge)
  const change = method === 'Cash' ? Math.max(0, receivedNum - effectiveAmountDue) : 0

  const fulfillmentComplete =
    fulfillmentMethod === 'TakeNow' ||
    (fulfillmentMethod === 'Delivery'
      ? deliveryChargeValid &&
        !!deliveryFields.scheduledDate &&
        !!deliveryFields.recipientName.trim() &&
        !!deliveryFields.deliveryAddress.trim()
      : !!pickupFields.scheduledDate && !!pickupFields.recipientName.trim())

  // Plain validation messages, independent of `fulfillmentAttempted` — FulfillmentDetailsFields
  // itself gates whether these are actually shown on its own `attempted` prop, mirroring the
  // per-schedule `showErrors` gate this file used before the allocation model was flattened.
  const deliveryErrors = {
    scheduledDate: deliveryFields.scheduledDate ? undefined : 'A delivery date is required.',
    recipientName: deliveryFields.recipientName.trim() ? undefined : 'Recipient name is required.',
    deliveryAddress: deliveryFields.deliveryAddress.trim() ? undefined : 'Recipient address is required.',
    deliveryCharge: deliveryChargeValid
      ? undefined
      : 'Enter a valid amount (0 or more, up to 2 decimal places).',
  }
  const pickupErrors = {
    scheduledDate: pickupFields.scheduledDate ? undefined : 'A pickup date is required.',
    recipientName: pickupFields.recipientName.trim() ? undefined : 'Recipient name is required.',
  }

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

        {/* How the customer receives the whole sale — chosen once, applying to every item — comes
            before payment method, so the cashier settles it first rather than discovering it after
            already picking how they're being paid. */}
        <div className="mb-4 rounded-lg border border-border-strong px-4 py-3.5">
          <p className="text-sm font-semibold text-text-secondary">How will the customer receive the order?</p>
          <div className="mt-3 grid grid-cols-3 gap-2">
            {(['TakeNow', 'Delivery', 'Pickup'] as const).map((m) => (
              <button
                key={m}
                type="button"
                aria-pressed={fulfillmentMethod === m}
                onClick={() => onFulfillmentMethodChange(m)}
                className={cn(
                  'rounded-lg border px-3 py-2 text-[13px] font-semibold transition-all',
                  fulfillmentMethod === m
                    ? 'border-primary-500 bg-primary-50 text-primary-700 shadow-sm'
                    : 'border-border-strong text-text-secondary hover:border-primary-200 hover:bg-surface-subtle',
                )}
              >
                {m === 'TakeNow' ? 'Take now' : m === 'Delivery' ? 'For delivery' : 'For pickup'}
              </button>
            ))}
          </div>

          {fulfillmentMethod === 'Delivery' && (
            <FulfillmentDetailsFields
              method="Delivery"
              values={deliveryFields}
              onChange={onDeliveryFieldsChange}
              deliveryCharge={deliveryCharge}
              onDeliveryChargeChange={onDeliveryChargeChange}
              errors={deliveryErrors}
              attempted={fulfillmentAttempted}
              disabled={submitting}
            />
          )}
          {fulfillmentMethod === 'Pickup' && (
            <FulfillmentDetailsFields
              method="Pickup"
              values={pickupFields}
              onChange={onPickupFieldsChange}
              errors={pickupErrors}
              attempted={fulfillmentAttempted}
              disabled={submitting}
            />
          )}
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
      </div>
    </Modal>
  )
}
