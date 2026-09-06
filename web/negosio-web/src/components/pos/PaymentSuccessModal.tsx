import { Check, Printer } from 'lucide-react'
import type { CheckoutPaymentInput, SaleResultDto } from '../../api/types'
import { PAYMENT_METHOD_LABELS, REFERENCE_LABELS } from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { Button, Modal } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  /** The just-completed sale, straight from the checkout response — never predicted or
   * reconstructed. Null-safe: renders nothing if somehow opened without one. */
  result: SaleResultDto | null
  /** The payment actually submitted for that sale — carries the method/tendered/reference detail
   * the checkout response itself doesn't echo back. */
  payment: CheckoutPaymentInput | null
  onNewTransaction: () => void
}

const RADIAL_DEGREES = [0, 45, 90, 135, 180, 225, 270, 315]

function SuccessGlyph() {
  return (
    <div className="relative mx-auto size-20">
      {RADIAL_DEGREES.map((deg) => (
        <span
          key={deg}
          aria-hidden="true"
          className="absolute inset-0 m-auto h-2.5 w-[3px] rounded-full bg-success/50"
          style={{ transform: `rotate(${deg}deg) translateY(-46px)` }}
        />
      ))}
      <div className="absolute inset-0 grid place-items-center rounded-full bg-success-light">
        <div className="grid size-12 place-items-center rounded-full bg-success">
          <Check className="size-7 text-white" strokeWidth={3} aria-hidden="true" />
        </div>
      </div>
    </div>
  )
}

function SummaryRow({ label, value, strong }: { label: string; value: string; strong?: boolean }) {
  return (
    <div className="flex items-center justify-between py-2">
      <dt className="text-text-muted">{label}</dt>
      <dd className={strong ? 'text-lg font-extrabold text-text-primary' : 'font-semibold text-text-primary'}>
        {value}
      </dd>
    </div>
  )
}

/**
 * The primary checkout confirmation — the background toast (still fired alongside this) is just a
 * lightweight echo, not the thing a cashier is meant to rely on. Only ever opened from the
 * checkout mutation's onSuccess, so `result`/`payment` are always the backend's real response and
 * the request that produced it — nothing here is predicted.
 */
export function PaymentSuccessModal({ open, onClose, result, payment, onNewTransaction }: Props) {
  if (!result) return null

  const isCash = payment?.method === 'Cash'
  const reference = payment?.referenceNumber?.trim()

  return (
    <Modal open={open} onClose={onClose} title="Payment successful" hideTitle size="sm">
      <div className="flex flex-col items-center pb-5 pt-1 text-center">
        <SuccessGlyph />
        <p className="mt-4 text-2xl font-extrabold text-text-primary">Payment successful</p>
        <p className="mt-1 font-mono text-sm font-semibold text-text-muted">Sale #{result.saleNumber}</p>
      </div>

      <div className="border-t border-border-light" />

      <dl className="my-5 divide-y divide-border-light rounded-2xl bg-surface-subtle px-4 py-1">
        <SummaryRow label="Amount paid" value={formatMoney(result.amountPaid)} strong />
        {payment && <SummaryRow label="Payment method" value={PAYMENT_METHOD_LABELS[payment.method]} />}
        {isCash && payment?.receivedAmount != null && (
          <SummaryRow label="Tendered" value={formatMoney(payment.receivedAmount)} />
        )}
        {isCash && <SummaryRow label="Change" value={formatMoney(result.changeDue)} />}
        {!isCash && reference && (
          <SummaryRow label={REFERENCE_LABELS[payment!.method] ?? 'Reference number'} value={reference} />
        )}
      </dl>

      <div className="space-y-2.5">
        <Button block size="lg" onClick={onNewTransaction}>
          New transaction
        </Button>
        <button
          type="button"
          onClick={() => window.open(`/sales/${result.saleId}/receipt?print=1`, '_blank', 'noopener')}
          className="flex h-12 w-full items-center justify-center gap-2 rounded-lg border border-primary-200 bg-white text-sm font-semibold text-primary-700 transition-colors hover:bg-primary-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
        >
          <Printer className="size-4" aria-hidden="true" />
          Print receipt
        </button>
        <a
          href={`/sales/${result.saleId}/receipt`}
          target="_blank"
          rel="noopener"
          className="block pt-1 text-center text-[13px] font-semibold text-primary-700 hover:underline"
        >
          View receipt
        </a>
      </div>
    </Modal>
  )
}
