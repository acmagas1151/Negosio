import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { deliveryReceiptsApi } from '../../api/deliveryReceipts'
import type { CreateDeliveryReceiptRequest, SaleItemFulfillmentDto } from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { todayLocalDateInput } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { Button, Modal, TextArea, TextField, useToast } from '../ui'

/** Optional prefill for "Schedule again" after a cancellation — recipient/address/contact/notes and
 * the item quantities the cancelled delivery carried. Quantities are re-clamped against each item's
 * CURRENT `availableToScheduleQuantity` at render time (never trusted as still-valid), since another
 * user may have consumed some of the released quantity in the meantime. */
export interface DeliveryPrefill {
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  deliveryNotes: string
  itemQuantities: Record<string, number> // saleItemId -> quantity
}

interface Props {
  open: boolean
  onClose: () => void
  saleId: string
  /** Every sale item with availableToScheduleQuantity > 0 right now — items fully scheduled/delivered
   * simply don't appear here, so there is nothing to accidentally over-claim. */
  availableItems: SaleItemFulfillmentDto[]
  prefill?: DeliveryPrefill
}

interface FieldErrors {
  scheduledDeliveryDate?: string
  recipientName?: string
  deliveryAddress?: string
}

export function CreateDeliveryReceiptModal({ open, onClose, saleId, availableItems, prefill }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [scheduledDeliveryDate, setScheduledDeliveryDate] = useState(todayLocalDateInput())
  const [recipientName, setRecipientName] = useState('')
  const [deliveryAddress, setDeliveryAddress] = useState('')
  const [contactNumber, setContactNumber] = useState('')
  const [deliveryNotes, setDeliveryNotes] = useState('')
  const [qty, setQty] = useState<Record<string, string>>({})
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setScheduledDeliveryDate(todayLocalDateInput())
    setRecipientName(prefill?.recipientName ?? '')
    setDeliveryAddress(prefill?.deliveryAddress ?? '')
    setContactNumber(prefill?.contactNumber ?? '')
    setDeliveryNotes(prefill?.deliveryNotes ?? '')
    // Clamp any prefilled quantity to what's actually available right now.
    const initialQty: Record<string, string> = {}
    for (const item of availableItems) {
      const wanted = prefill?.itemQuantities[item.saleItemId] ?? item.availableToScheduleQuantity
      initialQty[item.saleItemId] = String(Math.min(wanted, item.availableToScheduleQuantity))
    }
    setQty(initialQty)
    setFieldErrors({})
    // Deliberately only [open]: `availableItems` arrives from SaleDetailPage as an inline `.filter(...)`
    // expression (a new array reference on every render) and this modal stays mounted while closed, so
    // including it (or `prefill`) here would wipe in-progress edits on any unrelated parent re-render
    // while the modal is open. Reset only on the open/close transition; `prefill`/`availableItems` are
    // read fresh via closure at that moment since both call sites set them before flipping `open`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const mutation = useMutation({
    mutationFn: () => {
      const body: CreateDeliveryReceiptRequest = {
        scheduledDeliveryDate,
        recipientName: recipientName.trim(),
        deliveryAddress: deliveryAddress.trim(),
        contactNumber: contactNumber.trim() || null,
        deliveryNotes: deliveryNotes.trim() || null,
        items: availableItems
          .map((i) => ({ saleItemId: i.saleItemId, quantity: Number(qty[i.saleItemId] ?? '0') }))
          .filter((l) => l.quantity > 0),
      }
      return deliveryReceiptsApi.create(saleId, body)
    },
    onSuccess: (dr) => {
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-summary'] })
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-receipts'] })
      onClose()
      window.open('/delivery-receipts/' + dr.id + '?print=1', '_blank', 'noopener')
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      const next: FieldErrors = {}
      if (fields.recipientname) next.recipientName = fields.recipientname
      if (fields.deliveryaddress) next.deliveryAddress = fields.deliveryaddress
      if (fields.scheduleddeliverydate) next.scheduledDeliveryDate = fields.scheduleddeliverydate
      if (Object.keys(next).length > 0) {
        setFieldErrors(next)
        return
      }
      if (err instanceof ApiError && err.code === 'DELIVERY_QUANTITY_EXCEEDS_AVAILABLE') {
        toast('error', `${err.message} Refresh to see the current availability.`)
        qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-summary'] })
        return
      }
      toast(
        'error',
        err instanceof ApiError || err instanceof Error ? err.message : 'Could not create the delivery.',
      )
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setFieldErrors({})

    const next: FieldErrors = {}
    if (!scheduledDeliveryDate) next.scheduledDeliveryDate = 'A delivery date is required.'
    if (!recipientName.trim()) next.recipientName = 'Recipient name is required.'
    if (!deliveryAddress.trim()) next.deliveryAddress = 'Recipient address is required.'
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
      title={prefill ? 'Schedule delivery again' : 'Create delivery'}
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
          label="Scheduled delivery date"
          name="scheduledDeliveryDate"
          type="date"
          min={todayLocalDateInput()}
          value={scheduledDeliveryDate}
          onChange={(e) => setScheduledDeliveryDate(e.target.value)}
          error={fieldErrors.scheduledDeliveryDate || undefined}
          autoFocus
        />
        <TextField
          label="Recipient name"
          name="recipientName"
          value={recipientName}
          onChange={(e) => setRecipientName(e.target.value)}
          error={fieldErrors.recipientName || undefined}
        />
        <TextArea
          label="Recipient address"
          name="deliveryAddress"
          rows={2}
          value={deliveryAddress}
          onChange={(e) => setDeliveryAddress(e.target.value)}
          error={fieldErrors.deliveryAddress || undefined}
        />
        <TextField
          label="Contact number"
          name="contactNumber"
          value={contactNumber}
          onChange={(e) => setContactNumber(e.target.value)}
        />
        <TextArea
          label="Delivery notes"
          name="deliveryNotes"
          rows={2}
          value={deliveryNotes}
          onChange={(e) => setDeliveryNotes(e.target.value)}
        />

        <div className="space-y-2">
          <p className="text-sm font-semibold text-text-secondary">Items on this delivery</p>
          {availableItems.map((i) => (
            <div key={i.saleItemId} className="rounded-lg border border-border bg-surface-subtle p-3">
              <p className="text-sm font-medium text-text-primary">
                {i.productName}
                {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
              </p>
              <p className="mt-0.5 text-[12px] text-text-muted">
                Available to schedule: {formatQty(i.availableToScheduleQuantity)}
              </p>
              <label className="mt-2 flex items-center gap-2 text-[13px] text-text-secondary">
                Quantity
                <input
                  type="number"
                  min={0}
                  max={i.availableToScheduleQuantity}
                  step="0.001"
                  value={qty[i.saleItemId] ?? '0'}
                  onChange={(e) => setQty((p) => ({ ...p, [i.saleItemId]: e.target.value }))}
                  aria-label={`Delivery quantity for ${i.productName}`}
                  className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                />
              </label>
            </div>
          ))}
        </div>
      </form>
    </Modal>
  )
}
