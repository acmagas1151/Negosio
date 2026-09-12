import { useEffect, useState } from 'react'
import type { CheckoutPaymentInput, PaymentMethod } from '../../api/types'
import {
  POS_PAYMENT_METHODS,
  PAYMENT_METHOD_LABELS,
  REFERENCE_LABELS,
  isValidDeliveryChargeInput,
  parseDeliveryCharge,
  suggestCashButtons,
  type DeliverySchedule,
} from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField } from '../ui'
import { DeliveryDetailsFields } from './DeliveryDetailsFields'
import { PaymentMethodIcon } from './PaymentMethodIcon'

interface Props {
  open: boolean
  onClose: () => void
  amountDue: number
  submitting: boolean
  error: string | null
  onConfirm: (payment: CheckoutPaymentInput) => void
  /** Whether this sale is flagged for delivery — expands the inline delivery-details section. */
  forDelivery: boolean
  /** Tick / untick "For delivery". Unticking clears the delivery fields in the parent. */
  onToggleForDelivery: (next: boolean) => void
  cartLines: { variantId: string; name: string; variantName: string | null; quantity: number }[]
  deliveryRequiredByVariant: Record<string, number>
  onDeliveryRequiredChange: (variantId: string, quantity: number) => void
  schedules: DeliverySchedule[]
  onScheduleFieldChange: (key: string, patch: Partial<Omit<DeliverySchedule, 'key' | 'items'>>) => void
  onScheduleItemChange: (key: string, variantId: string, quantity: number) => void
  onAddSchedule: () => void
  onRemoveSchedule: (key: string) => void
  /** The typed delivery-charge string, owned by the parent for the same reason schedules is —
   * it must survive the modal closing and reopening after a failed-payment retry. */
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
  forDelivery,
  onToggleForDelivery,
  cartLines,
  deliveryRequiredByVariant,
  onDeliveryRequiredChange,
  schedules,
  onScheduleFieldChange,
  onScheduleItemChange,
  onAddSchedule,
  onRemoveSchedule,
  deliveryCharge,
  onDeliveryChargeChange,
}: Props) {
  const [method, setMethod] = useState<PaymentMethod>('Cash')
  const [received, setReceived] = useState('')
  const [reference, setReference] = useState('')
  // Delivery errors show only after a blocked confirm attempt, not while the cashier is still typing.
  const [deliveryAttempted, setDeliveryAttempted] = useState(false)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setMethod('Cash')
    setReceived('')
    setReference('')
    setDeliveryAttempted(false)
  }, [open])

  const receivedNum = Number(received)
  const deliveryChargeValid = !forDelivery || isValidDeliveryChargeInput(deliveryCharge)
  const effectiveAmountDue = amountDue + parseDeliveryCharge(forDelivery, deliveryCharge)
  const change = method === 'Cash' ? Math.max(0, receivedNum - effectiveAmountDue) : 0

  const activeSchedules = schedules.filter((s) => s.items.some((i) => i.quantity > 0))
  const deliveryComplete =
    !forDelivery ||
    (deliveryChargeValid &&
      activeSchedules.every((s) => s.scheduledDeliveryDate && s.recipientName.trim() && s.deliveryAddress.trim()))
  const deliveryErrors = forDelivery && deliveryAttempted ? { deliveryCharge: deliveryChargeValid ? undefined : 'Enter a valid amount (0 or more, up to 2 decimal places).' } : {}

  const paymentComplete =
    method !== 'Cash' || (received.trim() !== '' && receivedNum >= effectiveAmountDue)
  // The button stays enabled while delivery fields are incomplete — clicking it then surfaces the
  // inline errors rather than silently doing nothing. It only hard-disables for an incomplete
  // payment or an in-flight submit.
  const canConfirm = !submitting && paymentComplete

  const confirm = () => {
    if (submitting || !paymentComplete) return
    if (forDelivery && !deliveryComplete) {
      setDeliveryAttempted(true)
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

        <div
          className={cn(
            'mt-4 rounded-lg border border-border-strong',
            forDelivery ? 'px-4 py-3.5' : 'px-3 py-2.5',
          )}
        >
          <label className="flex items-center gap-2.5 text-sm font-semibold text-text-secondary">
            <input
              type="checkbox"
              checked={forDelivery}
              disabled={submitting}
              onChange={(e) => onToggleForDelivery(e.target.checked)}
              className="size-4 rounded border-border-strong text-primary-600 focus:ring-primary-500"
            />
            For delivery
          </label>
          {forDelivery && (
            <DeliveryDetailsFields
              cartLines={cartLines}
              deliveryCharge={deliveryCharge}
              onDeliveryChargeChange={onDeliveryChargeChange}
              deliveryRequiredByVariant={deliveryRequiredByVariant}
              onDeliveryRequiredChange={onDeliveryRequiredChange}
              schedules={schedules}
              onScheduleFieldChange={onScheduleFieldChange}
              onScheduleItemChange={onScheduleItemChange}
              onAddSchedule={onAddSchedule}
              onRemoveSchedule={onRemoveSchedule}
              errors={deliveryErrors}
              attempted={deliveryAttempted}
              disabled={submitting}
            />
          )}
        </div>
      </div>
    </Modal>
  )
}
