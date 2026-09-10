import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { deliveryReceiptsApi } from '../../api/deliveryReceipts'
import type { CreateDeliveryReceiptRequest, SaleDetailDto } from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { formatQty } from '../../lib/format'
import { Button, Modal, TextArea, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sale: SaleDetailDto
}

interface FieldErrors {
  recipientName?: string
  deliveryAddress?: string
  contactNumber?: string
  deliveryNotes?: string
}

export function CreateDeliveryReceiptModal({ open, onClose, sale }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [recipientName, setRecipientName] = useState('')
  const [deliveryAddress, setDeliveryAddress] = useState('')
  const [contactNumber, setContactNumber] = useState('')
  const [deliveryNotes, setDeliveryNotes] = useState('')
  const [qty, setQty] = useState<Record<string, string>>({})
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setRecipientName('')
    setDeliveryAddress('')
    setContactNumber('')
    setDeliveryNotes('')
    setQty({})
    setFieldErrors({})
  }, [open])

  const mutation = useMutation({
    mutationFn: () => {
      // A line's quantity defaults to the full sold qty; a 0 excludes the line. When every line
      // is still at full qty, send `items: undefined` so the backend delivers all sale lines.
      const lines = sale.items.map((i) => ({
        saleItemId: i.id,
        quantity: Number(qty[i.id] ?? String(i.quantity)),
      }))
      const allFull = sale.items.every(
        (i, idx) => lines[idx].quantity === i.quantity,
      )
      const items = allFull ? undefined : lines.filter((l) => l.quantity > 0)

      const body: CreateDeliveryReceiptRequest = {
        recipientName: recipientName.trim(),
        deliveryAddress: deliveryAddress.trim(),
        contactNumber: contactNumber.trim() || undefined,
        deliveryNotes: deliveryNotes.trim() || undefined,
        items,
      }
      return deliveryReceiptsApi.createForSale(sale.sale.id, body)
    },
    onSuccess: (dr) => {
      qc.invalidateQueries({ queryKey: ['sales', sale.sale.id, 'delivery-receipt'] })
      onClose()
      window.open('/delivery-receipts/' + dr.id + '?print=1', '_blank', 'noopener')
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      const next: FieldErrors = {}
      if (fields.recipientname) next.recipientName = fields.recipientname
      if (fields.deliveryaddress) next.deliveryAddress = fields.deliveryaddress
      if (fields.contactnumber) next.contactNumber = fields.contactnumber
      if (fields.deliverynotes) next.deliveryNotes = fields.deliverynotes
      if (Object.keys(next).length > 0) {
        setFieldErrors(next)
        return
      }
      toast(
        'error',
        err instanceof ApiError || err instanceof Error
          ? err.message
          : 'Could not create the delivery receipt.',
      )
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setFieldErrors({})

    const next: FieldErrors = {}
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
      title="Print delivery receipt"
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
      <form onSubmit={submit} className="space-y-4">
        <TextField
          label="Recipient name"
          name="recipientName"
          value={recipientName}
          onChange={(e) => setRecipientName(e.target.value)}
          error={fieldErrors.recipientName || undefined}
          autoFocus
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
          error={fieldErrors.contactNumber || undefined}
        />
        <TextArea
          label="Delivery notes"
          name="deliveryNotes"
          rows={2}
          value={deliveryNotes}
          onChange={(e) => setDeliveryNotes(e.target.value)}
          error={fieldErrors.deliveryNotes || undefined}
        />

        <div className="space-y-2">
          <p className="text-sm font-semibold text-text-secondary">Items to deliver</p>
          {sale.items.map((i) => (
            <div key={i.id} className="rounded-lg border border-border bg-surface-subtle p-3">
              <p className="text-sm font-medium text-text-primary">
                {i.productName}
                {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
              </p>
              <p className="mt-0.5 text-[12px] text-text-muted">Sold {formatQty(i.quantity)}</p>
              <label className="mt-2 flex items-center gap-2 text-[13px] text-text-secondary">
                Deliver qty
                <input
                  type="number"
                  min={0}
                  max={i.quantity}
                  step="0.001"
                  value={qty[i.id] ?? String(i.quantity)}
                  onChange={(e) => setQty((p) => ({ ...p, [i.id]: e.target.value }))}
                  aria-label={`Delivery quantity for ${i.productName}`}
                  className="h-9 w-20 rounded-lg border border-border-strong bg-white px-2 text-right text-sm focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
                />
              </label>
            </div>
          ))}
          <p className="text-[13px] text-text-muted">Set a line to 0 to leave it off the receipt.</p>
        </div>
      </form>
    </Modal>
  )
}
