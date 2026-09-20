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

/** The method the replacement schedule will be created as. DeliverLater's replacement is also a
 * Delivery (a same-method reschedule, per Task 3B) — only ConvertToPickup/PickupLater/
 * CustomerPickedUpInstead produce a Pickup. */
function replacementMethod(disposition: CancellationDisposition): 'Delivery' | 'Pickup' {
  return disposition === 'DeliverLater' || disposition === 'ConvertToDelivery' ? 'Delivery' : 'Pickup'
}

/** Every disposition except CustomerPickedUpInstead requires a new valid (today-or-future)
 * delivery/pickup date — CustomerPickedUpInstead records something that already happened and is
 * deliberately not date-gated (the backend doesn't gate it either — see
 * DeliveryReceiptService.CancelDeliveryAsync). */
function requiresFutureDate(disposition: CancellationDisposition): boolean {
  return disposition !== 'CustomerPickedUpInstead'
}

/** Mirrors DeliveryReceiptService.EnsureNotPastBusinessToday's message exactly, so a client-side
 * rejection reads identically to the server-side one it's standing in for. */
function pastDateMessage(disposition: CancellationDisposition): string {
  return `The scheduled ${replacementMethod(disposition).toLowerCase()} date cannot be in the past.`
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

  const isCustomerPickedUp = disposition === 'CustomerPickedUpInstead'
  const scheduleLabel = `${FULFILLMENT_METHOD_LABELS[schedule.method]} ${schedule.sequenceNumber}`

  const summary = useMemo(() => {
    switch (disposition) {
      case 'DeliverLater':
      case 'PickupLater':
      case 'ConvertToPickup':
      case 'ConvertToDelivery':
        return `${scheduleLabel} will be cancelled and a new ${replacementMethod(disposition)} created for ${formatScheduleDate(scheduledDate)}.`
      case 'CustomerPickedUpInstead':
        return `${scheduleLabel} will be cancelled and recorded as a new Pickup — Claimed — on ${formatScheduleDate(scheduledDate)}.`
    }
  }, [disposition, scheduleLabel, scheduledDate])

  const mutation = useMutation({
    mutationFn: (): Promise<CancellationResultDto> => {
      const trimmedReason = reason.trim()
      if (isDelivery) {
        const replacementFields = {
          scheduledDate,
          recipientName: recipientName.trim(),
          contactNumber: contactNumber.trim() || null,
          notes: notes.trim() || null,
        }
        const body: CancelDeliveryRequest = {
          reason: trimmedReason,
          disposition,
          // Pickup-shaped `replacement` — only for the two dispositions whose replacement is a Pickup.
          replacement:
            disposition === 'ConvertToPickup' || disposition === 'CustomerPickedUpInstead'
              ? replacementFields
              : null,
          // Delivery-shaped `rescheduledDelivery` (Task 3B) — only for DeliverLater's same-method
          // reschedule, which needs an address the Pickup-shaped `replacement` field has no room for.
          rescheduledDelivery:
            disposition === 'DeliverLater' ? { ...replacementFields, deliveryAddress: deliveryAddress.trim() } : null,
        }
        return fulfillmentApi.cancelDelivery(schedule.id, body)
      }
      const replacementFields = {
        scheduledDate,
        recipientName: recipientName.trim(),
        contactNumber: contactNumber.trim() || null,
        notes: notes.trim() || null,
      }
      const body: CancelPickupRequest = {
        reason: trimmedReason,
        disposition,
        // Delivery-shaped `replacement` — only for ConvertToDelivery, which needs an address.
        replacement:
          disposition === 'ConvertToDelivery' ? { ...replacementFields, deliveryAddress: deliveryAddress.trim() } : null,
        // Pickup-shaped `rescheduledPickup` (Task 3B) — only for PickupLater's same-method reschedule.
        rescheduledPickup: disposition === 'PickupLater' ? replacementFields : null,
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
      // `ValidationExtensions.ToCamelCase` lowercases only the first character of each dot-segment
      // (e.g. "RescheduledDelivery.ScheduledDate" -> "rescheduledDelivery.scheduledDate"), and
      // `fieldErrorsFrom` then lowercases the whole key on the client — so every lookup here must be
      // fully lowercase, dot-joined, no matter which C# property path produced it.
      if (fields['replacement.scheduleddate']) next.scheduledDate = fields['replacement.scheduleddate']
      if (fields['replacement.recipientname']) next.recipientName = fields['replacement.recipientname']
      if (fields['replacement.deliveryaddress']) next.deliveryAddress = fields['replacement.deliveryaddress']
      if (fields['rescheduleddelivery.scheduleddate']) next.scheduledDate = fields['rescheduleddelivery.scheduleddate']
      if (fields['rescheduleddelivery.recipientname']) next.recipientName = fields['rescheduleddelivery.recipientname']
      if (fields['rescheduleddelivery.deliveryaddress']) next.deliveryAddress = fields['rescheduleddelivery.deliveryaddress']
      if (fields['rescheduledpickup.scheduleddate']) next.scheduledDate = fields['rescheduledpickup.scheduleddate']
      if (fields['rescheduledpickup.recipientname']) next.recipientName = fields['rescheduledpickup.recipientname']
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
    if (!scheduledDate) {
      next.scheduledDate = `A ${replacementMethod(disposition).toLowerCase()} date is required.`
    } else if (requiresFutureDate(disposition) && scheduledDate < todayLocalDateInput()) {
      next.scheduledDate = pastDateMessage(disposition)
    }
    if (!recipientName.trim()) next.recipientName = 'Recipient name is required.'
    if ((disposition === 'ConvertToDelivery' || disposition === 'DeliverLater') && !deliveryAddress.trim()) {
      next.deliveryAddress = 'Recipient address is required.'
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
          {(disposition === 'ConvertToDelivery' || disposition === 'DeliverLater') && (
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
