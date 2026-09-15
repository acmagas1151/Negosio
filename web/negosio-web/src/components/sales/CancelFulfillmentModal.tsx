import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { fulfillmentApi } from '../../api/fulfillment'
import type {
  CancelDeliveryRequest,
  CancelPickupRequest,
  CancellationDisposition,
  CancellationResultDto,
  FulfillmentScheduleDto,
} from '../../api/types'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { CANCELLATION_DISPOSITION_LABELS, FULFILLMENT_METHOD_LABELS, todayLocalDateInput } from '../../lib/pos'
import { formatQty } from '../../lib/format'
import { Button, Callout, Modal, Select, TextArea, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  saleId: string
  /** The full schedule being cancelled — its `method` drives which disposition list applies, and
   * its recipient/contact/notes prefill the replacement sub-form for the "same customer" common case. */
  schedule: FulfillmentScheduleDto
}

/** Cancelling a Delivery. Deliberately NEVER includes a take-now disposition — TakeNow is a
 * checkout-time allocation only; "the customer already collected it" is represented by
 * CustomerPickedUpInstead (an already-Claimed Pickup), not by a take-now adjustment after the sale. */
const DELIVERY_DISPOSITIONS: CancellationDisposition[] = ['DeliverLater', 'ConvertToPickup', 'CustomerPickedUpInstead']

/** Cancelling a Pickup. Same rule: no take-now option, ever. */
const PICKUP_DISPOSITIONS: CancellationDisposition[] = ['PickupLater', 'ConvertToDelivery']

/** Dispositions that create a replacement schedule and need the sub-form. The two reschedule-only
 * dispositions (DeliverLater / PickupLater) release the quantity back to unscheduled and must send
 * `replacement: null` — the backend rejects a replacement payload for either of them. */
function needsReplacement(disposition: CancellationDisposition): boolean {
  return disposition === 'ConvertToPickup' || disposition === 'ConvertToDelivery' || disposition === 'CustomerPickedUpInstead'
}

/** The method the replacement schedule will be created as. */
function replacementMethod(disposition: CancellationDisposition): 'Delivery' | 'Pickup' {
  return disposition === 'ConvertToDelivery' ? 'Delivery' : 'Pickup'
}

/** Only ConvertToPickup/ConvertToDelivery are future-dated reschedules; CustomerPickedUpInstead
 * records something that already happened and is deliberately not date-gated (the backend doesn't
 * gate it either — see DeliveryReceiptService.CancelDeliveryAsync). */
function requiresFutureDate(disposition: CancellationDisposition): boolean {
  return disposition === 'ConvertToPickup' || disposition === 'ConvertToDelivery'
}

/** Mirrors DeliveryReceiptService.EnsureNotPastBusinessToday's message exactly, so a client-side
 * rejection reads identically to the server-side one it's standing in for. */
function pastDateMessage(disposition: CancellationDisposition): string {
  return disposition === 'ConvertToPickup'
    ? 'The scheduled pickup date cannot be in the past.'
    : 'The scheduled delivery date cannot be in the past.'
}

/** yyyy-MM-dd (a DateOnly, browser-local input value) -> "15 Sep 2026". Parsed with an explicit
 * midnight-local anchor so a negative-UTC-offset browser doesn't roll the date back a day the way
 * a bare `new Date("2026-09-15")` (parsed as UTC midnight) would. */
function formatScheduleDate(isoDate: string): string {
  return new Date(`${isoDate}T00:00:00`).toLocaleDateString('en-GB', {
    day: 'numeric',
    month: 'short',
    year: 'numeric',
  })
}

function itemsSummary(items: FulfillmentScheduleDto['items']): string {
  return items
    .map((i) => `${formatQty(i.quantity)} × ${i.productName}${i.variantName ? ` (${i.variantName})` : ''}`)
    .join(', ')
}

interface FieldErrors {
  reason?: string
  scheduledDate?: string
  recipientName?: string
  deliveryAddress?: string
}

export function CancelFulfillmentModal({ open, onClose, saleId, schedule }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const isDelivery = schedule.method === 'Delivery'
  const dispositions = isDelivery ? DELIVERY_DISPOSITIONS : PICKUP_DISPOSITIONS

  const [reason, setReason] = useState('')
  const [disposition, setDisposition] = useState<CancellationDisposition>(dispositions[0])
  const [scheduledDate, setScheduledDate] = useState(todayLocalDateInput())
  const [recipientName, setRecipientName] = useState(schedule.recipientName)
  const [deliveryAddress, setDeliveryAddress] = useState(schedule.deliveryAddress ?? '')
  const [contactNumber, setContactNumber] = useState(schedule.contactNumber ?? '')
  const [notes, setNotes] = useState(schedule.notes ?? '')
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({})
  const [error, setError] = useState('')

  const showReplacementForm = needsReplacement(disposition)
  const isCustomerPickedUp = disposition === 'CustomerPickedUpInstead'
  const scheduleLabel = `${FULFILLMENT_METHOD_LABELS[schedule.method]} ${schedule.sequenceNumber}`

  const summary = useMemo(() => {
    switch (disposition) {
      case 'DeliverLater':
      case 'PickupLater':
        return `${scheduleLabel} will be cancelled. ${itemsSummary(schedule.items)} returns to unscheduled.`
      case 'ConvertToPickup':
        return `${scheduleLabel} will be cancelled and a new Pickup created for ${formatScheduleDate(scheduledDate)}.`
      case 'ConvertToDelivery':
        return `${scheduleLabel} will be cancelled and a new Delivery created for ${formatScheduleDate(scheduledDate)}.`
      case 'CustomerPickedUpInstead':
        return `${scheduleLabel} will be cancelled and recorded as a new Pickup — Claimed — on ${formatScheduleDate(scheduledDate)}.`
    }
  }, [disposition, scheduleLabel, scheduledDate, schedule.items])

  const mutation = useMutation({
    mutationFn: (): Promise<CancellationResultDto> => {
      const trimmedReason = reason.trim()
      if (isDelivery) {
        const body: CancelDeliveryRequest = {
          reason: trimmedReason,
          disposition,
          replacement: showReplacementForm
            ? {
                scheduledDate,
                recipientName: recipientName.trim(),
                contactNumber: contactNumber.trim() || null,
                notes: notes.trim() || null,
              }
            : null,
        }
        return fulfillmentApi.cancelDelivery(schedule.id, body)
      }
      const body: CancelPickupRequest = {
        reason: trimmedReason,
        disposition,
        replacement: showReplacementForm
          ? {
              scheduledDate,
              recipientName: recipientName.trim(),
              deliveryAddress: deliveryAddress.trim(),
              contactNumber: contactNumber.trim() || null,
              notes: notes.trim() || null,
            }
          : null,
      }
      return fulfillmentApi.cancelPickup(schedule.id, body)
    },
    onSuccess: (result) => {
      qc.invalidateQueries({ queryKey: ['sales', saleId] })
      qc.invalidateQueries({ queryKey: ['sales', saleId, 'fulfillment-summary'] })
      const cancelledLabel = `${FULFILLMENT_METHOD_LABELS[result.cancelled.method]} ${result.cancelled.sequenceNumber}`
      if (result.replacement) {
        const replacementLabel = `${FULFILLMENT_METHOD_LABELS[result.replacement.method]} ${result.replacement.sequenceNumber}`
        const claimedNote = result.replacement.status === 'Completed' ? ' (Claimed)' : ''
        toast('success', `${cancelledLabel} cancelled. ${replacementLabel}${claimedNote} created.`)
      } else {
        toast('success', `${cancelledLabel} cancelled.`)
      }
      onClose()
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      const next: FieldErrors = {}
      if (fields.reason) next.reason = fields.reason
      if (fields['replacement.scheduleddate']) next.scheduledDate = fields['replacement.scheduleddate']
      if (fields['replacement.recipientname']) next.recipientName = fields['replacement.recipientname']
      if (fields['replacement.deliveryaddress']) next.deliveryAddress = fields['replacement.deliveryaddress']
      if (Object.keys(next).length > 0) {
        setFieldErrors(next)
        return
      }
      if (err instanceof ApiError && err.code === 'DELIVERY_RECEIPT_CONCURRENCY_CONFLICT') {
        setError('This record was changed by someone else. Close this dialog and try again.')
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not cancel this fulfillment.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setFieldErrors({})
    setError('')

    const next: FieldErrors = {}
    if (!reason.trim()) next.reason = 'A cancellation reason is required.'
    if (showReplacementForm) {
      if (!scheduledDate) {
        next.scheduledDate = `A ${replacementMethod(disposition).toLowerCase()} date is required.`
      } else if (requiresFutureDate(disposition) && scheduledDate < todayLocalDateInput()) {
        next.scheduledDate = pastDateMessage(disposition)
      }
      if (!recipientName.trim()) next.recipientName = 'Recipient name is required.'
      if (disposition === 'ConvertToDelivery' && !deliveryAddress.trim()) {
        next.deliveryAddress = 'Recipient address is required.'
      }
    }
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
      title={`Cancel ${scheduleLabel}`}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Keep it
          </Button>
          <Button
            size="sm"
            variant="destructive"
            onClick={submit}
            loading={mutation.isPending}
            disabled={!reason.trim()}
          >
            Confirm
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="max-h-[62vh] space-y-4 overflow-y-auto pr-1">
        {error && <Callout tone="error">{error}</Callout>}

        <Select
          label="What happens to these items?"
          name="disposition"
          value={disposition}
          onChange={(e) => setDisposition(e.target.value as CancellationDisposition)}
        >
          {dispositions.map((d) => (
            <option key={d} value={d}>
              {CANCELLATION_DISPOSITION_LABELS[d]}
            </option>
          ))}
        </Select>

        {showReplacementForm && (
          <div className="space-y-4 rounded-lg border border-border bg-surface-subtle p-3">
            <TextField
              label={isCustomerPickedUp ? 'Date collected' : `Scheduled ${replacementMethod(disposition).toLowerCase()} date`}
              name="scheduledDate"
              type="date"
              min={requiresFutureDate(disposition) ? todayLocalDateInput() : undefined}
              value={scheduledDate}
              onChange={(e) => setScheduledDate(e.target.value)}
              error={fieldErrors.scheduledDate || undefined}
            />
            <TextField
              label="Recipient name"
              name="recipientName"
              value={recipientName}
              onChange={(e) => setRecipientName(e.target.value)}
              error={fieldErrors.recipientName || undefined}
            />
            {disposition === 'ConvertToDelivery' && (
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
              label={`${replacementMethod(disposition)} notes`}
              name="notes"
              rows={2}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
            />
          </div>
        )}

        <TextArea
          label="Reason"
          name="reason"
          rows={2}
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          error={fieldErrors.reason || undefined}
          autoFocus
        />

        <Callout tone="info">{summary}</Callout>
      </form>
    </Modal>
  )
}
