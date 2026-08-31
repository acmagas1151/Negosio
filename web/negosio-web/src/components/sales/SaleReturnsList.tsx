import type { SaleReturnDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'

export function SaleReturnsList({ returns }: { returns: SaleReturnDto[] }) {
  if (returns.length === 0) return null

  return (
    <div className="space-y-3">
      {returns.map((r) => (
        <div key={r.id} className="rounded-xl border border-border bg-surface p-4">
          <div className="flex flex-wrap items-baseline justify-between gap-2">
            <span className="font-semibold text-text-primary">
              {r.returnNumber}
              <span className="ml-1.5 font-normal text-text-muted">for sale {r.originalSaleNumber}</span>
            </span>
            <span className="text-[13px] text-text-muted">
              {new Date(r.createdAtUtc).toLocaleString()} · {r.createdByName}
            </span>
          </div>
          <p className="mt-1 text-[13px] text-text-secondary">{r.reason}</p>
          <ul className="mt-2 space-y-0.5 text-[13px] text-text-secondary">
            {r.items.map((it) => (
              <li key={it.id} className="flex justify-between">
                <span>
                  {it.productName} × {formatQty(it.quantity)}
                  {it.restocked ? '' : ' (not restocked)'}
                </span>
                <span>{formatMoney(it.refundAmount)}</span>
              </li>
            ))}
          </ul>
          <div className="mt-2 flex justify-between border-t border-border pt-2 text-sm font-semibold text-text-primary">
            <span>
              Refund
              {r.refunds.length > 0
                ? ` · ${r.refunds.map((rf) => rf.method).join(' + ')}`
                : ''}
            </span>
            <span>{formatMoney(r.totalRefund)}</span>
          </div>
        </div>
      ))}
    </div>
  )
}
