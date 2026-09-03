import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { salesApi } from '../api/pos'
import { PAYMENT_METHOD_LABELS, VOID_INELIGIBLE_MESSAGES } from '../lib/pos'
import { formatMoney } from '../lib/format'
import { hasReturnableQty } from '../lib/returns'
import { useCan } from '../lib/useCan'
import { ReturnModal } from '../components/sales/ReturnModal'
import { SaleItemsTable } from '../components/sales/SaleItemsTable'
import { SaleReturnsList } from '../components/sales/SaleReturnsList'
import { StatusBadge } from '../components/sales/StatusBadge'
import { VoidSaleModal } from '../components/sales/VoidSaleModal'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { Button, ErrorState, LoadingState } from '../components/ui'

export default function SaleDetailPage() {
  const { id = '' } = useParams()
  const canRefund = useCan('refund:manage')
  const canVoidCapability = useCan('sales:void')
  const [returnOpen, setReturnOpen] = useState(false)
  const [voidOpen, setVoidOpen] = useState(false)

  const query = useQuery({
    queryKey: ['sales', id],
    queryFn: () => salesApi.get(id),
    enabled: !!id,
  })
  const branchesQuery = useQuery({
    queryKey: ['branches', 'sales-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const multiBranch = (branchesQuery.data?.length ?? 0) > 1

  return (
    <DashboardLayout title="Sale">
      <div className="space-y-6">
        <Link
          to="/sales"
          className="inline-flex items-center gap-1.5 text-[13px] font-semibold text-text-secondary hover:text-text-primary"
        >
          <ArrowLeft className="size-4" aria-hidden="true" />
          Back to sales
        </Link>

        {query.isPending ? (
          <LoadingState />
        ) : query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : (
          (() => {
            const d = query.data
            const canStartReturn =
              canRefund &&
              (d.sale.status === 'Completed' || d.sale.status === 'PartiallyRefunded') &&
              hasReturnableQty(d.items)
            return (
              <>
                <div className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
                  <div>
                    <div className="flex items-center gap-2">
                      <h1 className="text-2xl font-bold text-text-primary">Sale #{d.sale.saleNumber}</h1>
                      <StatusBadge status={d.sale.status} />
                    </div>
                    <p className="mt-1 text-[13px] text-text-muted">
                      {new Date(d.sale.createdAtUtc).toLocaleString()} · {d.sale.cashierName}
                      {multiBranch ? ` · ${d.sale.branchName}` : ''}
                    </p>
                  </div>
                  <div className="flex flex-col items-end gap-1.5">
                    <div className="flex gap-2">
                      {canStartReturn && (
                        <Button variant="secondary" size="sm" onClick={() => setReturnOpen(true)}>
                          Start return
                        </Button>
                      )}
                      {canVoidCapability && d.sale.status === 'Completed' && d.canVoid && (
                        <Button variant="destructive" size="sm" onClick={() => setVoidOpen(true)}>
                          Void sale
                        </Button>
                      )}
                      <Link to={`/sales/${d.sale.id}/receipt`}>
                        <Button variant="secondary" size="sm">
                          Print receipt
                        </Button>
                      </Link>
                    </div>
                    {canVoidCapability && d.sale.status === 'Completed' && !d.canVoid && d.voidIneligibilityCode && (
                      <p className="text-[13px] text-text-muted">
                        {VOID_INELIGIBLE_MESSAGES[d.voidIneligibilityCode] ?? 'This sale cannot be voided.'}
                      </p>
                    )}
                  </div>
                </div>

                {d.sale.status === 'Voided' && (
                  <div className="rounded-xl border border-danger/20 bg-danger-light p-4 text-sm">
                    <p className="font-semibold text-danger-strong">Voided</p>
                    <dl className="mt-2 space-y-1 text-text-secondary">
                      <div>
                        <dt className="inline font-medium text-text-primary">Voided by: </dt>
                        <dd className="inline">{d.voidedByName}</dd>
                      </div>
                      {d.approvedByName && (
                        <div>
                          <dt className="inline font-medium text-text-primary">Approved by: </dt>
                          <dd className="inline">{d.approvedByName}</dd>
                        </div>
                      )}
                      <div>
                        <dt className="inline font-medium text-text-primary">Reason: </dt>
                        <dd className="inline">{d.voidReason}</dd>
                      </div>
                      <div>
                        <dt className="inline font-medium text-text-primary">Voided at: </dt>
                        <dd className="inline">
                          {d.voidedAtUtc ? new Date(d.voidedAtUtc).toLocaleString() : ''}
                        </dd>
                      </div>
                    </dl>
                  </div>
                )}

                <div className="grid gap-4 lg:grid-cols-[1fr_260px]">
                  <div className="space-y-4">
                    <SaleItemsTable items={d.items} />

                    <div className="rounded-xl border border-border bg-surface p-4">
                      <h2 className="mb-2 text-sm font-semibold text-text-primary">Payments</h2>
                      <ul className="space-y-1 text-sm text-text-secondary">
                        {d.payments.map((p) => (
                          <li key={p.id} className="flex justify-between">
                            <span>
                              {PAYMENT_METHOD_LABELS[p.method]}
                              {p.referenceNumber ? ` · ${p.referenceNumber}` : ''}
                              {p.receivedAmount != null
                                ? ` · received ${formatMoney(p.receivedAmount)}`
                                : ''}
                              {p.changeAmount != null && p.changeAmount > 0
                                ? ` · change ${formatMoney(p.changeAmount)}`
                                : ''}
                            </span>
                            <span className="font-medium text-text-primary">
                              {formatMoney(p.amount)}
                            </span>
                          </li>
                        ))}
                      </ul>
                    </div>
                  </div>

                  <dl className="h-fit space-y-1.5 rounded-xl border border-border bg-surface p-4 text-sm">
                    <div className="flex justify-between text-text-secondary">
                      <dt>Subtotal</dt>
                      <dd>{formatMoney(d.subtotal)}</dd>
                    </div>
                    <div className="flex justify-between text-text-secondary">
                      <dt>Discount</dt>
                      <dd>−{formatMoney(d.discountTotal)}</dd>
                    </div>
                    <div className="flex justify-between text-text-secondary">
                      <dt>Tax</dt>
                      <dd>{formatMoney(d.taxTotal)}</dd>
                    </div>
                    <div className="flex justify-between border-t border-border pt-1.5 text-base font-bold text-text-primary">
                      <dt>Total</dt>
                      <dd>{formatMoney(d.sale.grandTotal)}</dd>
                    </div>
                    <div className="flex justify-between pt-1 text-text-secondary">
                      <dt>Amount paid</dt>
                      <dd>{formatMoney(d.amountPaid)}</dd>
                    </div>
                    <div className="flex justify-between text-text-secondary">
                      <dt>Change due</dt>
                      <dd>{formatMoney(d.changeDue)}</dd>
                    </div>
                  </dl>
                </div>

                {d.returns.length > 0 && (
                  <div className="space-y-3">
                    <h2 className="text-lg font-bold text-text-primary">Returns</h2>
                    <SaleReturnsList returns={d.returns} />
                  </div>
                )}

                <ReturnModal open={returnOpen} onClose={() => setReturnOpen(false)} sale={d} />
                <VoidSaleModal
                  open={voidOpen}
                  onClose={() => setVoidOpen(false)}
                  sale={d}
                  onVoided={() => query.refetch()}
                />
              </>
            )
          })()
        )}
      </div>
    </DashboardLayout>
  )
}
