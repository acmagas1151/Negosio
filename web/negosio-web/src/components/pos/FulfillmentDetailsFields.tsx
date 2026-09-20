import type { FulfillmentMethod } from '../../api/types'
import type { FulfillmentDetails } from '../../lib/pos'
import { FULFILLMENT_METHOD_LABELS, todayLocalDateInput } from '../../lib/pos'
import { TextArea, TextField } from '../ui'

interface Props {
  /** Never 'TakeNow' — take-now has no schedule at all. */
  method: FulfillmentMethod
  values: FulfillmentDetails
  onChange: (patch: Partial<FulfillmentDetails>) => void
  /** Delivery only — Pickup has no delivery charge, so the caller omits both for that method. */
  deliveryCharge?: string
  onDeliveryChargeChange?: (value: string) => void
  errors: { recipientName?: string; deliveryAddress?: string; scheduledDate?: string; deliveryCharge?: string }
  /** True once the cashier has attempted to confirm payment with an incomplete schedule — gates
   * showing the "required" errors, matching PaymentModal's existing `attempted` convention. */
  attempted: boolean
  disabled?: boolean
}

/**
 * The single delivery-or-pickup detail form in the Payment modal — one flat set of fields for the
 * whole sale (date/recipient/address-for-delivery/contact/notes), now that a sale has at most one
 * schedule covering every item at full quantity (see the plan's whole-sale fulfillment model).
 * There is no item picker here: every item on the sale is included automatically, so this never
 * asks the cashier to select products or quantities.
 */
export function FulfillmentDetailsFields({
  method,
  values,
  onChange,
  deliveryCharge,
  onDeliveryChargeChange,
  errors,
  attempted,
  disabled,
}: Props) {
  const isDelivery = method === 'Delivery'
  const methodLabel = FULFILLMENT_METHOD_LABELS[method]
  const isFree = isDelivery && errors.deliveryCharge == null && Number(deliveryCharge) === 0

  return (
    <div className="mt-3 space-y-4 border-t border-border pt-3">
      {isDelivery && (
        <div>
          <TextField
            label="Delivery charge (₱)"
            name="deliveryCharge"
            type="number"
            min={0}
            step="0.01"
            value={deliveryCharge ?? ''}
            onChange={(e) => onDeliveryChargeChange?.(e.target.value)}
            error={(attempted && errors.deliveryCharge) || undefined}
            disabled={disabled}
          />
          {isFree && <p className="mt-1 text-[12px] text-text-muted">Free delivery</p>}
        </div>
      )}

      <TextField
        label={`Scheduled ${methodLabel.toLowerCase()} date *`}
        name="scheduledDate"
        type="date"
        min={todayLocalDateInput()}
        value={values.scheduledDate}
        onChange={(e) => onChange({ scheduledDate: e.target.value })}
        error={(attempted && errors.scheduledDate) || undefined}
        disabled={disabled}
      />
      <TextField
        label="Recipient name *"
        name="recipientName"
        value={values.recipientName}
        onChange={(e) => onChange({ recipientName: e.target.value })}
        error={(attempted && errors.recipientName) || undefined}
        disabled={disabled}
      />
      {isDelivery && (
        <TextArea
          label="Recipient address *"
          name="deliveryAddress"
          rows={2}
          value={values.deliveryAddress}
          onChange={(e) => onChange({ deliveryAddress: e.target.value })}
          error={(attempted && errors.deliveryAddress) || undefined}
          disabled={disabled}
        />
      )}
      <div className="grid gap-x-3 sm:grid-cols-2">
        <TextField
          label="Contact number"
          name="contactNumber"
          value={values.contactNumber}
          onChange={(e) => onChange({ contactNumber: e.target.value })}
          disabled={disabled}
        />
        <TextField
          label={`${methodLabel} notes (optional)`}
          name="notes"
          value={values.notes}
          onChange={(e) => onChange({ notes: e.target.value })}
          disabled={disabled}
        />
      </div>
    </div>
  )
}
