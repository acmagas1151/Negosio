import { useState } from 'react'
import type { DeliverySchedule } from '../../lib/pos'
import { scheduledQuantityFor, todayLocalDateInput } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { Button, TextArea, TextField } from '../ui'

interface CartLineInfo {
  variantId: string
  name: string
  variantName: string | null
  quantity: number
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
  cartLines: CartLineInfo[]
  deliveryCharge: string
  onDeliveryChargeChange: (value: string) => void
  /** variantId -> quantity of that line marked for delivery (0 = entirely Take-now). Every line not
   * present here is treated as 0. */
  deliveryRequiredByVariant: Record<string, number>
  onDeliveryRequiredChange: (variantId: string, quantity: number) => void
  schedules: DeliverySchedule[]
  onScheduleFieldChange: (key: string, patch: Partial<Omit<DeliverySchedule, 'key' | 'items'>>) => void
  onScheduleItemChange: (key: string, variantId: string, quantity: number) => void
  onAddSchedule: () => void
  onRemoveSchedule: (key: string) => void
  errors: { deliveryCharge?: string }
  /** True once the cashier has attempted to confirm payment with an incomplete schedule — gates
   * showing per-schedule "required" errors, matching PaymentModal's existing `deliveryAttempted`
   * convention for the charge/recipient fields. */
  attempted: boolean
  disabled?: boolean
}

export function DeliveryDetailsFields({
  cartLines,
  deliveryCharge,
  onDeliveryChargeChange,
  deliveryRequiredByVariant,
  onDeliveryRequiredChange,
  schedules,
  onScheduleFieldChange,
  onScheduleItemChange,
  onAddSchedule,
  onRemoveSchedule,
  errors,
  attempted,
  disabled,
}: Props) {
  const isFree = errors.deliveryCharge == null && Number(deliveryCharge) === 0
  const deliveryItemLines = cartLines.filter((l) => (deliveryRequiredByVariant[l.variantId] ?? 0) > 0)

  return (
    <div className="mt-3 space-y-4 border-t border-border pt-3">
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

      <div className="space-y-2">
        <p className="text-sm font-semibold text-text-secondary">Items for delivery</p>
        {cartLines.map((l) => {
          const required = deliveryRequiredByVariant[l.variantId] ?? 0
          return (
            <div key={l.variantId} className="flex items-center justify-between gap-3 rounded-lg border border-border bg-surface-subtle px-3 py-2">
              <div className="min-w-0">
                <p className="truncate text-sm font-medium text-text-primary">
                  {l.name}
                  {l.variantName && <span className="text-text-muted"> · {l.variantName}</span>}
                </p>
                <p className="text-[12px] text-text-muted">
                  Sold {formatQty(l.quantity)} · Take-now {formatQty(l.quantity - required)}
                </p>
              </div>
              <label className="flex shrink-0 items-center gap-2 text-[13px] text-text-secondary">
                For delivery
                <QuantityInput
                  value={required}
                  max={l.quantity}
                  onCommit={(v) => onDeliveryRequiredChange(l.variantId, v)}
                  disabled={disabled}
                  ariaLabel={`Delivery quantity for ${l.name}`}
                  className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                />
              </label>
            </div>
          )
        })}
      </div>

      {deliveryItemLines.length > 0 && (
        <div className="space-y-3">
          <p className="text-sm font-semibold text-text-secondary">Delivery schedules</p>
          {schedules.map((s, idx) => {
            const showErrors = attempted && s.items.some((i) => i.quantity > 0)
            return (
              <div key={s.key} className="space-y-3 rounded-xl border border-border-strong p-3">
                <div className="flex items-center justify-between">
                  <p className="text-[13px] font-semibold text-text-primary">Delivery {idx + 1}</p>
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
                  label="Scheduled delivery date *"
                  name={`scheduledDeliveryDate-${s.key}`}
                  type="date"
                  min={todayLocalDateInput()}
                  value={s.scheduledDeliveryDate}
                  onChange={(e) => onScheduleFieldChange(s.key, { scheduledDeliveryDate: e.target.value })}
                  error={showErrors && !s.scheduledDeliveryDate ? 'A delivery date is required.' : undefined}
                  disabled={disabled}
                />
                <TextField
                  label="Recipient name *"
                  name={`recipientName-${s.key}`}
                  value={s.recipientName}
                  onChange={(e) => onScheduleFieldChange(s.key, { recipientName: e.target.value })}
                  error={showErrors && !s.recipientName.trim() ? 'Recipient name is required.' : undefined}
                  disabled={disabled}
                />
                <TextArea
                  label="Recipient address *"
                  name={`deliveryAddress-${s.key}`}
                  rows={2}
                  value={s.deliveryAddress}
                  onChange={(e) => onScheduleFieldChange(s.key, { deliveryAddress: e.target.value })}
                  error={showErrors && !s.deliveryAddress.trim() ? 'Recipient address is required.' : undefined}
                  disabled={disabled}
                />
                <div className="grid gap-x-3 sm:grid-cols-2">
                  <TextField
                    label="Contact number"
                    name={`contactNumber-${s.key}`}
                    value={s.contactNumber}
                    onChange={(e) => onScheduleFieldChange(s.key, { contactNumber: e.target.value })}
                    disabled={disabled}
                  />
                  <TextField
                    label="Delivery notes (optional)"
                    name={`deliveryNotes-${s.key}`}
                    value={s.deliveryNotes}
                    onChange={(e) => onScheduleFieldChange(s.key, { deliveryNotes: e.target.value })}
                    disabled={disabled}
                  />
                </div>

                <div className="space-y-1.5">
                  {deliveryItemLines.map((l) => {
                    const required = deliveryRequiredByVariant[l.variantId] ?? 0
                    const claimedElsewhere = scheduledQuantityFor(schedules, l.variantId, s.key)
                    const cap = Math.max(0, required - claimedElsewhere)
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
                          ariaLabel={`Quantity of ${l.name} on Delivery ${idx + 1}`}
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
            + Add another delivery schedule
          </Button>
        </div>
      )}
    </div>
  )
}
