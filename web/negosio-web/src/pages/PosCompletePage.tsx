import { useQuery } from '@tanstack/react-query'
import { Link, useLocation, useParams } from 'react-router-dom'
import { CheckCircle2 } from 'lucide-react'
import { salesApi } from '../api/pos'
import type { SaleResultDto } from '../api/types'
import { formatMoney } from '../lib/format'
import { Button, ErrorState, LoadingState } from '../components/ui'

export default function PosCompletePage() {
  const { saleId = '' } = useParams()
  const location = useLocation()
  const passed = (location.state as { result?: SaleResultDto } | null)?.result

  const detailQuery = useQuery({
    queryKey: ['sales', saleId],
    queryFn: () => salesApi.get(saleId),
    enabled: !passed && !!saleId,
  })

  let saleNumber: string | undefined
  let grandTotal = 0
  let amountPaid = 0
  let changeDue = 0

  if (passed) {
    saleNumber = passed.saleNumber
    grandTotal = passed.grandTotal
    amountPaid = passed.amountPaid
    changeDue = passed.changeDue
  } else if (detailQuery.data) {
    const d = detailQuery.data
    saleNumber = d.sale.saleNumber
    grandTotal = d.sale.grandTotal
    amountPaid = d.amountPaid
    changeDue = d.changeDue
  }

  const body = () => {
    if (!passed && detailQuery.isPending) return <LoadingState />
    if (!passed && detailQuery.isError) {
      return <ErrorState message={(detailQuery.error as Error).message} />
    }
    return (
      <div className="w-full max-w-sm space-y-5 rounded-2xl border border-border bg-surface p-8 text-center shadow-card-lg">
        <span className="mx-auto flex size-14 items-center justify-center rounded-full bg-success-light text-success-strong">
          <CheckCircle2 className="size-7" aria-hidden="true" />
        </span>
        <div>
          <p className="text-[13px] uppercase tracking-wide text-text-muted">Sale complete</p>
          <p className="text-xl font-bold text-text-primary">Sale #{saleNumber}</p>
        </div>
        <dl className="space-y-1 text-sm">
          <div className="flex justify-between">
            <dt className="text-text-muted">Total</dt>
            <dd className="font-semibold text-text-primary">{formatMoney(grandTotal)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-text-muted">Paid</dt>
            <dd className="text-text-secondary">{formatMoney(amountPaid)}</dd>
          </div>
          <div className="flex justify-between">
            <dt className="text-text-muted">Change</dt>
            <dd className="font-semibold text-text-primary">{formatMoney(changeDue)}</dd>
          </div>
        </dl>
        <div className="space-y-2">
          <Link to="/pos" className="block">
            <Button block>New sale</Button>
          </Link>
          <div className="flex gap-2">
            <Link to={`/sales/${saleId}`} className="flex-1">
              <Button block variant="secondary" size="sm">
                View sale
              </Button>
            </Link>
            <Link to={`/sales/${saleId}/receipt?print=1`} className="flex-1">
              <Button block variant="secondary" size="sm">
                Print receipt
              </Button>
            </Link>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="flex min-h-screen items-center justify-center bg-background p-4">{body()}</div>
  )
}
