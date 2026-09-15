import { useState } from 'react'
import type { FulfillmentMethod } from '../../api/types'
import type { FulfillmentSchedule } from '../../lib/pos'
import { FULFILLMENT_METHOD_LABELS, scheduledQuantityFor, todayLocalDateInput } from '../../lib/pos'
import { Button, TextArea, TextField } from '../ui'

interface CartLineInfo {
  variantId: string
  name: string
  variantName: string | null
  /** How much of this line is required for THIS method specifically — the pool the per-schedule
   * item allocation below draws from. The caller (PaymentModal) has already filtered this list down
   * to lines with a non-zero requirement for `method`. */
  requiredQuantity: number
}

interface QuantityInputProps {
  value: number
  max: number
  onCommit: (quantity: number) => void
  ariaLabel: string
  disabled?: boolean
  className?: string
}

/** A quantity `<input type="number">` that holds its own raw-string draft while being edited and
 * only parses/clamps/commits on blur (or Enter) — mirrors CartItem.tsx's `qtyDraft` pattern. A
 * controlled numeric input that re-renders with a coerced number on every keystroke breaks
 * fractional entry: typing "1", then ".", then "5" would otherwise collapse to "15" because the
 * DOM value snaps back to the coerced integer before the "5" lands. Since this app has real
 * fractional-quantity (weighed) products, that would be a real usability bug, not a cosmetic one.
 * Each `.map()`-rendered row gets its own instance, so each has its own independent draft state —
 * no shared state to leak across rows. */
function QuantityInput({ value, max, onCommit, ariaLabel, disabled, className }: QuantityInputProps) {
  // While being edited this holds the raw typed string; `null` means "show the committed value".
  const [draft, setDraft] = useState<string | null>(null)

  // Commit from the field's own value (not React state) so a programmatic set-then-blur or a
  // paste-then-tab still applies — the committed number is always what the input actually holds.
  const commit = (raw: string) => {
    setDraft(null)
    const trimmed = raw.trim()
    const n = Number(trimmed)
    if (trimmed !== '' && Number.isFinite(n)) {
      onCommit(Math.max(0, Math.min(n, max)))
    }
  }

  return (
    <input
      type="number"
      min={0}
      max={Math.max(max, value)}
      step="0.001"
      value={draft ?? String(value)}
      disabled={disabled}
      onChange={(e) => setDraft(e.target.value)}
      onBlur={(e) => commit(e.currentTarget.value)}
      onKeyDown={(e) => {
        if (e.key === 'Enter') {
          e.preventDefault()
          e.currentTarget.blur()
        }
      }}
      aria-label={ariaLabel}
      className={className}
    />
  )
}

interface Props {
  /** Never 'TakeNow' — take-now has no schedule at all. */
  method: FulfillmentMethod
  cartLines: CartLineInfo[]
  /** Delivery only — Pickup has no delivery charge, so the caller omits both for that method. */
  deliveryCharge?: string
  onDeliveryChargeChange?: (value: string) => void
  schedules: FulfillmentSchedule[]
  onScheduleFieldChange: (key: string, patch: Partial<Omit<FulfillmentSchedule, 'key' | 'items'>>) => void
  onScheduleItemChange: (key: string, variantId: string, quantity: number) => void
  onAddSchedule: () => void
  onRemoveSchedule: (key: string) => void
  errors: { deliveryCharge?: string }
  /** True once the cashier has attempted to confirm payment with an incomplete schedule — gates
   * showing per-schedule "required" errors, matching PaymentModal's existing `attempted`
   * convention for the charge/recipient fields. */
  attempted: boolean
  disabled?: boolean
}

export function FulfillmentDetailsFields({
  method,
  cartLines,
  deliveryCharge,
  onDeliveryChargeChange,
  schedules,
  onScheduleFieldChange,
  onScheduleItemChange,
  onAddSchedule,
  onRemoveSchedule,
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
            error={errors.deliveryCharge || undefined}
            disabled={disabled}
          />
          {isFree && <p className="mt-1 text-[12px] text-text-muted">Free delivery</p>}
        </div>
      )}

      {cartLines.length > 0 && (
        <div className="space-y-3">
          <p className="text-sm font-semibold text-text-secondary">{methodLabel} schedules</p>
          {schedules.map((s, idx) => {
            const showErrors = attempted && s.items.some((i) => i.quantity > 0)
            return (
              <div key={s.key} className="space-y-3 rounded-xl border border-border-strong p-3">
                <div className="flex items-center justify-between">
                  <p className="text-[13px] font-semibold text-text-primary">
                    {methodLabel} {idx + 1}
                  </p>
                  {schedules.length > 1 && (
                    <button
                      type="button"
                      onClick={() => onRemoveSchedule(s.key)}
                      disabled={disabled}
                      className="text-[12px] font-semibold text-danger hover:underline"
                    >
                      Remove
                    </button>
                  )}
                </div>

                <TextField
                  label={`Scheduled ${methodLabel.toLowerCase()} date *`}
                  name={`scheduledDate-${method}-${s.key}`}
                  type="date"
                  min={todayLocalDateInput()}
                  value={s.scheduledDate}
                  onChange={(e) => onScheduleFieldChange(s.key, { scheduledDate: e.target.value })}
                  error={
                    showErrors && !s.scheduledDate
                      ? `A ${methodLabel.toLowerCase()} date is required.`
                      : undefined
                  }
                  disabled={disabled}
                />
                <TextField
                  label="Recipient name *"
                  name={`recipientName-${method}-${s.key}`}
                  value={s.recipientName}
                  onChange={(e) => onScheduleFieldChange(s.key, { recipientName: e.target.value })}
                  error={showErrors && !s.recipientName.trim() ? 'Recipient name is required.' : undefined}
                  disabled={disabled}
                />
                {isDelivery && (
                  <TextArea
                    label="Recipient address *"
                    name={`deliveryAddress-${method}-${s.key}`}
                    rows={2}
                    value={s.deliveryAddress}
                    onChange={(e) => onScheduleFieldChange(s.key, { deliveryAddress: e.target.value })}
                    error={
                      showErrors && !s.deliveryAddress.trim() ? 'Recipient address is required.' : undefined
                    }
                    disabled={disabled}
                  />
                )}
                <div className="grid gap-x-3 sm:grid-cols-2">
                  <TextField
                    label="Contact number"
                    name={`contactNumber-${method}-${s.key}`}
                    value={s.contactNumber}
                    onChange={(e) => onScheduleFieldChange(s.key, { contactNumber: e.target.value })}
                    disabled={disabled}
                  />
                  <TextField
                    label={`${methodLabel} notes (optional)`}
                    name={`notes-${method}-${s.key}`}
                    value={s.notes}
                    onChange={(e) => onScheduleFieldChange(s.key, { notes: e.target.value })}
                    disabled={disabled}
                  />
                </div>

                <div className="space-y-1.5">
                  {cartLines.map((l) => {
                    const claimedElsewhere = scheduledQuantityFor(schedules, l.variantId, s.key)
                    const cap = Math.max(0, l.requiredQuantity - claimedElsewhere)
                    const value = s.items.find((i) => i.variantId === l.variantId)?.quantity ?? 0
                    return (
                      <label
                        key={l.variantId}
                        className="flex items-center justify-between gap-3 text-[13px] text-text-secondary"
                      >
                        <span className="truncate">
                          {l.name}
                          {l.variantName ? ` · ${l.variantName}` : ''}
                        </span>
                        <QuantityInput
                          value={value}
                          max={cap}
                          onCommit={(v) => onScheduleItemChange(s.key, l.variantId, v)}
                          disabled={disabled}
                          ariaLabel={`Quantity of ${l.name} on ${methodLabel} ${idx + 1}`}
                          className="h-8 w-16 shrink-0 rounded-md border border-border-strong bg-white px-2 text-right text-[13px] focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                        />
                      </label>
                    )
                  })}
                </div>
              </div>
            )
          })}
          <Button type="button" variant="secondary" size="sm" onClick={onAddSchedule} disabled={disabled}>
            + Add another {methodLabel.toLowerCase()}
          </Button>
        </div>
      )}
    </div>
  )
}
