import { useEffect, useState } from 'react'
import type { CheckoutPaymentInput, PaymentMethod } from '../../api/types'
import { POS_PAYMENT_METHODS, PAYMENT_METHOD_LABELS, REFERENCE_LABELS, suggestCashButtons } from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField } from '../ui'
import { PaymentMethodIcon } from './PaymentMethodIcon'

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

      <div className="mb-4 grid grid-cols-5 gap-2">
        {POS_PAYMENT_METHODS.map((m) => (
          <button
            key={m}
            type="button"
            aria-pressed={method === m}
            onClick={() => setMethod(m)}
            className={cn(
              'flex flex-col items-center gap-1.5 rounded-xl border px-1 py-3 transition-all',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500',
              method === m
                ? 'border-primary-500 bg-primary-50 shadow-sm'
                : 'border-border-strong hover:border-primary-200 hover:bg-surface-subtle',
            )}
          >
            <PaymentMethodIcon
              method={m}
              className={cn('size-6', method === m ? 'text-primary-700' : 'text-text-muted')}
            />
            <span
              className={cn(
                'text-[12px] font-semibold',
                method === m ? 'text-primary-700' : 'text-text-secondary',
              )}
            >
              {PAYMENT_METHOD_LABELS[m]}
            </span>
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
          label={`${REFERENCE_LABELS[method] ?? 'Reference number'} (optional)`}
          name="reference"
          value={reference}
          onChange={(e) => setReference(e.target.value)}
          autoFocus
        />
      )}
    </Modal>
  )
}
