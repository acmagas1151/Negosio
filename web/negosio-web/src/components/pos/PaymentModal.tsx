import { useEffect, useState } from 'react'
import type { CheckoutPaymentInput, PaymentMethod } from '../../api/types'
import { POS_PAYMENT_METHODS, PAYMENT_METHOD_LABELS, suggestCashButtons } from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  amountDue: number
  submitting: boolean
  error: string | null
  onConfirm: (payment: CheckoutPaymentInput) => void
}

export function PaymentModal({ open, onClose, amountDue, submitting, error, onConfirm }: Props) {
  const [method, setMethod] = useState<PaymentMethod>('Cash')
  const [received, setReceived] = useState('')
  const [reference, setReference] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setMethod('Cash')
    setReceived('')
    setReference('')
  }, [open])

  const receivedNum = Number(received)
  const change = method === 'Cash' ? Math.max(0, receivedNum - amountDue) : 0
  const canConfirm =
    !submitting && (method !== 'Cash' || (received.trim() !== '' && receivedNum >= amountDue))

  const confirm = () => {
    if (!canConfirm) return
    if (method === 'Cash') {
      onConfirm({ method: 'Cash', receivedAmount: receivedNum })
    } else {
      onConfirm({ method, amount: amountDue, referenceNumber: reference.trim() || null })
    }
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Take payment"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={submitting}>
            Cancel
          </Button>
          <Button size="sm" onClick={confirm} loading={submitting} disabled={!canConfirm}>
            Confirm payment
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <div className="mb-4 rounded-lg bg-surface-subtle px-3 py-3 text-center">
        <p className="text-[12px] uppercase tracking-wide text-text-muted">Amount due</p>
        <p className="text-2xl font-bold text-text-primary">{formatMoney(amountDue)}</p>
      </div>

      <div className="mb-4 flex flex-wrap gap-1.5">
        {POS_PAYMENT_METHODS.map((m) => (
          <button
            key={m}
            type="button"
            aria-pressed={method === m}
            onClick={() => setMethod(m)}
            className={cn(
              'rounded-lg px-3 py-1.5 text-sm font-medium transition-colors',
              method === m
                ? 'bg-primary-600 text-white'
                : 'border border-border-strong text-text-secondary hover:bg-surface-subtle',
            )}
          >
            {PAYMENT_METHOD_LABELS[m]}
          </button>
        ))}
      </div>

      {method === 'Cash' ? (
        <div className="space-y-3">
          <TextField
            label="Cash received"
            name="received"
            type="number"
            min={0}
            step="0.01"
            value={received}
            onChange={(e) => setReceived(e.target.value)}
            autoFocus
          />
          <div className="flex flex-wrap gap-1.5">
            {suggestCashButtons(amountDue).map((amt) => (
              <button
                key={amt}
                type="button"
                onClick={() => setReceived(String(amt))}
                className="rounded-lg border border-border-strong px-3 py-1 text-sm font-semibold text-text-secondary hover:bg-surface-subtle"
              >
                {formatMoney(amt)}
              </button>
            ))}
          </div>
          <div className="flex justify-between rounded-lg bg-surface-subtle px-3 py-2 text-sm">
            <span className="text-text-muted">Change</span>
            <span className="font-bold text-text-primary">{formatMoney(change)}</span>
          </div>
        </div>
      ) : (
        <TextField
          label="Reference number (optional)"
          name="reference"
          value={reference}
          onChange={(e) => setReference(e.target.value)}
          autoFocus
        />
      )}
    </Modal>
  )
}
