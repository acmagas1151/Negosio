import type { DeliveryFields } from '../../lib/pos'
import { TextArea, TextField } from '../ui'

interface Props {
  values: DeliveryFields
  onChange: (patch: Partial<DeliveryFields>) => void
  errors: { recipientName?: string; deliveryAddress?: string }
  disabled?: boolean
}

/** Presentational: the soft nested card with the four delivery inputs. All state and validation
 * live in the parent (PaymentModal) so the values survive a failed-payment retry. */
export function DeliveryDetailsFields({ values, onChange, errors, disabled }: Props) {
  return (
    <div className="mt-3 space-y-2 rounded-xl border border-primary-200 bg-primary-50/70 p-3">
      <p className="text-sm font-bold text-text-primary">Delivery details</p>
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
