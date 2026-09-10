import { useEffect, useState } from 'react'
import { Button, Modal, TextArea, TextField } from '../ui'

/** What the cashier fills in when a POS sale is "for delivery". Collected before payment is
 * confirmed and held by PosTerminal; the actual delivery receipt is created only after the sale
 * exists. A POS delivery always covers the whole sale, so there is no per-line quantity here. */
export interface DeliveryDetails {
  recipientName: string
  deliveryAddress: string
  contactNumber: string
  deliveryNotes: string
}

interface Props {
  open: boolean
  /** Cancel — nothing is saved. */
  onClose: () => void
  /** Save the (trimmed) details back to the caller. */
  onSave: (details: DeliveryDetails) => void
  /** Repopulate the form when re-opened to edit; null for a fresh entry. */
  initial: DeliveryDetails | null
}

interface FieldErrors {
  recipientName?: string
  deliveryAddress?: string
}

export function DeliveryDetailsModal({ open, onClose, onSave, initial }: Props) {
  const [recipientName, setRecipientName] = useState('')
  const [deliveryAddress, setDeliveryAddress] = useState('')
  const [contactNumber, setContactNumber] = useState('')
  const [deliveryNotes, setDeliveryNotes] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setRecipientName(initial?.recipientName ?? '')
    setDeliveryAddress(initial?.deliveryAddress ?? '')
    setContactNumber(initial?.contactNumber ?? '')
    setDeliveryNotes(initial?.deliveryNotes ?? '')
    setErrors({})
  }, [open, initial])

  const save = (e: React.FormEvent) => {
    e.preventDefault()
    const next: FieldErrors = {}
    if (!recipientName.trim()) next.recipientName = 'Recipient name is required.'
    if (!deliveryAddress.trim()) next.deliveryAddress = 'Recipient address is required.'
    if (Object.keys(next).length > 0) {
      setErrors(next)
      return
    }
    onSave({
      recipientName: recipientName.trim(),
      deliveryAddress: deliveryAddress.trim(),
      contactNumber: contactNumber.trim(),
      deliveryNotes: deliveryNotes.trim(),
    })
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Delivery details"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose}>
            Cancel
          </Button>
          <Button size="sm" onClick={save}>
            Save
          </Button>
        </>
      }
    >
      <form onSubmit={save} className="space-y-4">
        <p className="text-[13px] text-text-muted">
          The delivery receipt is created together with the sale when you confirm payment.
        </p>
        <TextField
          label="Recipient name"
          name="recipientName"
          value={recipientName}
          onChange={(e) => setRecipientName(e.target.value)}
          error={errors.recipientName || undefined}
          autoFocus
        />
        <TextArea
          label="Recipient address"
          name="deliveryAddress"
          rows={2}
          value={deliveryAddress}
          onChange={(e) => setDeliveryAddress(e.target.value)}
          error={errors.deliveryAddress || undefined}
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
      </form>
    </Modal>
  )
}
