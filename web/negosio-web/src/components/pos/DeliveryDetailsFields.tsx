import type { DeliveryFields } from '../../lib/pos'
import { TextArea, TextField } from '../ui'

interface Props {
  values: DeliveryFields
  onChange: (patch: Partial<DeliveryFields>) => void
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
  errors: { recipientName?: string; deliveryAddress?: string; deliveryCharge?: string }
  disabled?: boolean
}

/** Presentational: the delivery inputs, rendered directly inside the payment modal's
 * "For delivery" section (no card of their own). All state and validation live in the parent
 * (PaymentModal) so the values survive a failed-payment retry. */
export function DeliveryDetailsFields({
  values,
  onChange,
  deliveryCharge,
  onDeliveryChargeChange,
  errors,
  disabled,
}: Props) {
  const isFree = errors.deliveryCharge == null && Number(deliveryCharge) === 0

  return (
    <div className="mt-3 space-y-3 border-t border-border pt-3">
      <TextField
        label="Recipient name *"
        name="recipientName"
        placeholder="Enter recipient name"
        value={values.recipientName}
        onChange={(e) => onChange({ recipientName: e.target.value })}
        error={errors.recipientName || undefined}
        disabled={disabled}
      />
      <TextArea
        label="Recipient address *"
        name="deliveryAddress"
        rows={2}
        placeholder="Enter delivery address"
        value={values.deliveryAddress}
        onChange={(e) => onChange({ deliveryAddress: e.target.value })}
        error={errors.deliveryAddress || undefined}
        disabled={disabled}
      />
      <div>
        <TextField
          label="Delivery charge (₱)"
          name="deliveryCharge"
          type="number"
          min={0}
          step="0.01"
          value={deliveryCharge}
          onChange={(e) => onDeliveryChargeChange(e.target.value)}
          error={errors.deliveryCharge || undefined}
          disabled={disabled}
        />
        {isFree && <p className="mt-1 text-[12px] text-text-muted">Free delivery</p>}
      </div>
      <div className="grid gap-x-3 sm:grid-cols-2">
        <TextField
          label="Contact number"
          name="contactNumber"
          placeholder="Enter contact number"
          value={values.contactNumber}
          onChange={(e) => onChange({ contactNumber: e.target.value })}
          disabled={disabled}
        />
        <TextField
          label="Delivery notes (optional)"
          name="deliveryNotes"
          placeholder="e.g. Leave at the front desk"
          value={values.deliveryNotes}
          onChange={(e) => onChange({ deliveryNotes: e.target.value })}
          disabled={disabled}
        />
      </div>
    </div>
  )
}
