import { Wallet } from 'lucide-react'
import type { PaymentMethodBreakdownDto } from '../../api/types'
import { PAYMENT_METHOD_LABELS } from '../../lib/pos'
import { formatMoney } from '../../lib/format'
import { EmptyState } from '../ui'

interface Props {
  methods: PaymentMethodBreakdownDto[]
}

export function PaymentMethodBreakdown({ methods }: Props) {
  if (methods.length === 0) {
    return (
      <EmptyState icon={Wallet} title="No payments in this range" description="Try a wider date range or different filters." />
    )
  }

  return (
    <ul className="space-y-3">
      {methods.map((m) => (
        <li key={m.method}>
          <div className="mb-1 flex items-baseline justify-between gap-2">
            <span className="text-sm font-semibold text-text-primary">{PAYMENT_METHOD_LABELS[m.method]}</span>
            <span className="text-sm font-semibold text-text-primary">{formatMoney(m.amount)}</span>
          </div>
          <div className="h-1.5 overflow-hidden rounded-full bg-surface-subtle">
            <div
              className="h-full rounded-full bg-primary-500"
              style={{ width: `${Math.min(100, m.percentage)}%` }}
            />
          </div>
          <p className="mt-1 text-[12px] text-text-muted">
            {m.paymentCount} payment{m.paymentCount === 1 ? '' : 's'} · {m.percentage}%
          </p>
        </li>
      ))}
    </ul>
  )
}
