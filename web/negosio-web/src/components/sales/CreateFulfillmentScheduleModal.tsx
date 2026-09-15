import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { fulfillmentApi } from '../../api/fulfillment'
import type { CreateDeliveryReceiptRequest, CreatePickupRequest, SaleItemFulfillmentDto } from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { todayLocalDateInput } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { Button, Modal, TextArea, TextField, useToast } from '../ui'

/** This modal only ever creates a *new* schedule — reschedule-after-cancellation is now handled by
 * the cancellation dialog's disposition (e.g. "Convert to pickup"), which creates the replacement
 * in the same action. There is deliberately no prefill/"schedule again" path here any more. */
type ScheduleMethod = 'Delivery' | 'Pickup'

interface Props {
  open: boolean
  onClose: () => void
  saleId: string
  method: ScheduleMethod
  /** Every sale item with a nonzero *unscheduled* quantity for `method` right now — items fully
   * scheduled/delivered/claimed simply don't appear here, so there is nothing to accidentally
   * over-claim. */
  availableItems: SaleItemFulfillmentDto[]
}

interface FieldErrors {
  scheduledDate?: string
  recipientName?: string
  deliveryAddress?: string
}

/** Reads the method-specific unscheduled bucket straight off the summary DTO — a field selection,
 * not a computation, so it doesn't create a second source of truth for the 8 server-derived numbers. */
function unscheduledQuantity(method: ScheduleMethod, item: SaleItemFulfillmentDto): number {
  return method === 'Delivery' ? item.deliveryUnscheduledQuantity : item.pickupUnscheduledQuantity
}

export function CreateFulfillmentScheduleModal({ open, onClose, saleId, method, availableItems }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const isDelivery = method === 'Delivery'
  const methodLabel = isDelivery ? 'delivery' : 'pickup'

  const [scheduledDate, setScheduledDate] = useState(todayLocalDateInput())
  const [recipientName, setRecipientName] = useState('')
  const [deliveryAddress, setDeliveryAddress] = useState('')
  const [contactNumber, setContactNumber] = useState('')
  const [notes, setNotes] = useState('')
  const [qty, setQty] = useState<Record<string, string>>({})
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setScheduledDate(todayLocalDateInput())
    setRecipientName('')
    setDeliveryAddress('')
    setContactNumber('')
    setNotes('')
    const initialQty: Record<string, string> = {}
    for (const item of availableItems) {
      initialQty[item.saleItemId] = String(unscheduledQuantity(method, item))
    }
    setQty(initialQty)
    setFieldErrors({})
    // Deliberately only [open]: `availableItems` arrives from SaleDetailPage as an inline
    // `.filter(...)` expression (a new array reference on every render) and this modal stays mounted
    // while closed, so including it (or `method`) here would wipe in-progress edits on any unrelated
    // parent re-render while the modal is open. Reset only on the open/close transition — the call
    // site sets `method`/`availableItems` before flipping `open`, so the closure reads them fresh at
    // that moment. This is the exact bug (and fix) already shipped once in this codebase.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const mutation = useMutation({
    mutationFn: () => {
      const items = availableItems
        .map((i) => ({ saleItemId: i.saleItemId, quantity: Number(qty[i.saleItemId] ?? '0') }))
        .filter((l) => l.quantity > 0)
      if (isDelivery) {
        const body: CreateDeliveryReceiptRequest = {
          scheduledDate,
          recipientName: recipientName.trim(),
          deliveryAddress: deliveryAddress.trim(),
          contactNumber: contactNumber.trim() || null,
          notes: notes.trim() || null,
          items,
        }
        return fulfillmentApi.createDelivery(saleId, body)
      }
      const body: CreatePickupRequest = {
        scheduledDate,
        recipientName: recipientName.trim(),
        contactNumber: contactNumber.trim() || null,
        notes: notes.trim() || null,
        items,
      }
      return fulfillmentApi.createPickup(saleId, body)
    },
    onSuccess: (schedule) => {
      qc.invalidateQueries({ queryKey: ['sales', saleId] })
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'fulfillment-summary'] })
      onClose()
      window.open(`/delivery-receipts/${schedule.id}?print=1`, '_blank', 'noopener')
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      const next: FieldErrors = {}
      if (fields.recipientname) next.recipientName = fields.recipientname
      if (fields.deliveryaddress) next.deliveryAddress = fields.deliveryaddress
      if (fields.scheduleddate) next.scheduledDate = fields.scheduleddate
      if (Object.keys(next).length > 0) {
        setFieldErrors(next)
        return
      }
      const exceedsCode = isDelivery ? 'DELIVERY_QUANTITY_EXCEEDS_AVAILABLE' : 'PICKUP_QUANTITY_EXCEEDS_AVAILABLE'
      if (err instanceof ApiError && err.code === exceedsCode) {
        toast('error', `${err.message} Refresh to see the current availability.`)
        qc.invalidateQueries({ queryKey: ['sales', saleId, 'fulfillment-summary'] })
        return
      }
      toast(
        'error',
        err instanceof ApiError || err instanceof Error ? err.message : `Could not create the ${methodLabel}.`,
      )
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setFieldErrors({})

    const next: FieldErrors = {}
    if (!scheduledDate) next.scheduledDate = `A ${methodLabel} date is required.`
    if (!recipientName.trim()) next.recipientName = 'Recipient name is required.'
    if (isDelivery && !deliveryAddress.trim()) next.deliveryAddress = 'Recipient address is required.'
    if (Object.keys(next).length > 0) {
      setFieldErrors(next)
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={isDelivery ? 'Create delivery' : 'Create pickup'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            Create &amp; print
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="max-h-[62vh] space-y-4 overflow-y-auto pr-1">
        <TextField
          label={`Scheduled ${methodLabel} date`}
          name="scheduledDate"
          type="date"
          min={todayLocalDateInput()}
          value={scheduledDate}
          onChange={(e) => setScheduledDate(e.target.value)}
          error={fieldErrors.scheduledDate || undefined}
          autoFocus
        />
        <TextField
          label="Recipient name"
          name="recipientName"
          value={recipientName}
          onChange={(e) => setRecipientName(e.target.value)}
          error={fieldErrors.recipientName || undefined}
        />
        {isDelivery && (
          <TextArea
            label="Recipient address"
            name="deliveryAddress"
            rows={2}
            value={deliveryAddress}
            onChange={(e) => setDeliveryAddress(e.target.value)}
            error={fieldErrors.deliveryAddress || undefined}
          />
        )}
        <TextField
          label="Contact number"
          name="contactNumber"
          value={contactNumber}
          onChange={(e) => setContactNumber(e.target.value)}
        />
        <TextArea
          label={`${isDelivery ? 'Delivery' : 'Pickup'} notes`}
          name="notes"
          rows={2}
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
        />

        <div className="space-y-2">
          <p className="text-sm font-semibold text-text-secondary">Items on this {methodLabel}</p>
          {availableItems.map((i) => {
            const max = unscheduledQuantity(method, i)
            return (
              <div key={i.saleItemId} className="rounded-lg border border-border bg-surface-subtle p-3">
                <p className="text-sm font-medium text-text-primary">
                  {i.productName}
                  {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
                </p>
                <p className="mt-0.5 text-[12px] text-text-muted">Available to schedule: {formatQty(max)}</p>
                <label className="mt-2 flex items-center gap-2 text-[13px] text-text-secondary">
                  Quantity
                  <input
                    type="number"
                    min={0}
                    max={max}
                    step="0.001"
                    value={qty[i.saleItemId] ?? '0'}
                    onChange={(e) => setQty((p) => ({ ...p, [i.saleItemId]: e.target.value }))}
                    aria-label={`${isDelivery ? 'Delivery' : 'Pickup'} quantity for ${i.productName}`}
                    className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                  />
                </label>
              </div>
            )
          })}
        </div>
      </form>
    </Modal>
  )
}
