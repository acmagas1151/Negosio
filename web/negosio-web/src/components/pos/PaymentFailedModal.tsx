import { AlertTriangle, X } from 'lucide-react'
import type { CheckoutPaymentInput } from '../../api/types'
import { PAYMENT_METHOD_LABELS, REFERENCE_LABELS } from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { Button, Modal } from '../ui'
import { PaymentMethodIcon } from './PaymentMethodIcon'

interface Props {
  open: boolean
  onClose: () => void
  onTryAgain: () => void
  /** The sale total the failed attempt was for — the cart is never cleared on failure, so this is
   * still exactly what the cashier is trying to charge. */
  amount: number
  /** The payment that was attempted, if we got far enough to build one. */
  payment: CheckoutPaymentInput | null
  /** Already a safe, cashier-facing message — never a raw exception/stack trace. */
  message: string
  /**
   * True only when the backend guarantees nothing was persisted (e.g. the request was rejected
   * before or without writing a Sale). False for a genuinely ambiguous outcome (e.g. the response
   * was lost to a network error) — the DB may or may not have completed the sale, so this never
   * claims "not charged" when that isn't actually known.
   */
  certainNotCharged: boolean
}

const RADIAL_DEGREES = [0, 45, 90, 135, 180, 225, 270, 315]

function ErrorGlyph() {
  return (
    <div className="relative mx-auto size-20">
      {RADIAL_DEGREES.map((deg) => (
        <span
          key={deg}
          aria-hidden="true"
          className="absolute inset-0 m-auto h-2.5 w-[3px] rounded-full bg-danger/50"
          style={{ transform: `rotate(${deg}deg) translateY(-46px)` }}
        />
      ))}
      <div className="absolute inset-0 grid place-items-center rounded-full bg-danger-light">
        <div className="grid size-12 place-items-center rounded-full bg-danger">
          <X className="size-7 text-white" strokeWidth={3} aria-hidden="true" />
        </div>
      </div>
    </div>
  )
}

function SummaryRow({ label, value, tone }: { label: string; value: string; tone?: 'danger' }) {
  return (
    <div className="flex items-start justify-between gap-4 py-2">
      <dt className="text-text-muted">{label}</dt>
      <dd className={tone === 'danger' ? 'text-right font-semibold text-danger-strong' : 'font-semibold text-text-primary'}>
        {value}
      </dd>
    </div>
  )
}

/**
 * Mirrors PaymentSuccessModal's layout/spacing/backdrop exactly (same Modal shell, same glyph
 * treatment, same details-card pattern) with a red error rather than a green success — only ever
 * opened from the checkout mutation's onError, and only for genuine payment-attempt failures
 * (network/connectivity, a concurrency conflict, or a truly unexpected error). Insufficient
 * inventory, discount approval, and register-session-lost each already have their own targeted
 * recovery flow and don't route through here.
 */
export function PaymentFailedModal({ open, onClose, onTryAgain, amount, payment, message, certainNotCharged }: Props) {
  const isCash = payment?.method === 'Cash'
  const reference = payment?.referenceNumber?.trim()

  return (
    <Modal open={open} onClose={onClose} title="Payment failed" hideTitle size="sm">
      <div className="flex flex-col items-center pb-5 pt-1 text-center">
        <ErrorGlyph />
        <p className="mt-4 text-2xl font-extrabold text-text-primary">Payment failed</p>
        <p className="mt-2 text-[13px] text-text-secondary">
          We couldn&rsquo;t complete the payment. Please check the details below and try again.
        </p>
      </div>

      <div className="border-t border-border-light" />

      <dl className="my-5 divide-y divide-border-light rounded-2xl bg-surface-subtle px-4 py-1">
        <SummaryRow label="Amount" value={formatMoney(amount)} />
        {payment && (
          <div className="flex items-center justify-between py-2">
            <dt className="text-text-muted">Payment method</dt>
            <dd className="flex items-center gap-1.5 font-semibold text-text-primary">
              <PaymentMethodIcon method={payment.method} className="size-4" />
              {PAYMENT_METHOD_LABELS[payment.method]}
            </dd>
          </div>
        )}
        {!isCash && reference && (
          <SummaryRow label={REFERENCE_LABELS[payment!.method] ?? 'Reference number'} value={reference} />
        )}
        <SummaryRow label="Error message" value={message} tone="danger" />
      </dl>

      <div className="mb-5 flex gap-2.5 rounded-xl bg-danger-light px-4 py-3">
        <AlertTriangle className="mt-0.5 size-4 shrink-0 text-danger-strong" aria-hidden="true" />
        <p className="text-[13px] text-danger-strong">
          <span className="font-bold">
            {certainNotCharged ? 'No amount has been charged.' : 'Payment was not confirmed.'}
          </span>{' '}
          {certainNotCharged
            ? 'You can try again or choose a different payment method.'
            : 'Please verify the payment status before retrying.'}
        </p>
      </div>

      <div className="flex gap-2.5">
        <Button variant="secondary" size="lg" onClick={onClose} className="flex-1">
          Close
        </Button>
        <Button size="lg" onClick={onTryAgain} className="flex-[1.5]">
          Try again
        </Button>
      </div>
    </Modal>
  )
}
