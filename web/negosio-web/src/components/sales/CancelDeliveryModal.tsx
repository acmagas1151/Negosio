import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { deliveryReceiptsApi } from '../../api/deliveryReceipts'
import { Button, Callout, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  saleId: string
  deliveryReceiptId: string
  sequenceNumber: number
}

export function CancelDeliveryModal({ open, onClose, saleId, deliveryReceiptId, sequenceNumber }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const [reason, setReason] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setReason('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () => deliveryReceiptsApi.cancel(deliveryReceiptId, { reason }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-summary'] })
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'delivery-receipts'] })
      toast('success', `Delivery ${sequenceNumber} cancelled`)
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'DELIVERY_RECEIPT_CONCURRENCY_CONFLICT') {
        setError('This delivery was changed by someone else. Close this dialog and try again.')
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not cancel this delivery.')
    },
  })

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`Cancel Delivery ${sequenceNumber}`}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Keep it
          </Button>
          <Button
            size="sm"
            variant="destructive"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!reason.trim()}
          >
            Cancel delivery
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}
      <p className="mb-3 text-[13px] text-text-muted">
        This releases its items back to Unscheduled so they can be put on a new delivery. The Sale
        itself and its delivery charge are unaffected. This record is kept as history and cannot be
        reactivated — scheduling again creates a new one.
      </p>
      <TextField
        label="Reason"
        name="reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        autoFocus
      />
    </Modal>
  )
}
