import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { ApiError } from '../api/client'
import { deliveryReceiptsApi } from '../api/deliveryReceipts'
import { salesApi } from '../api/pos'
import { PAYMENT_METHOD_LABELS, SALE_FULFILLMENT_STATUS_LABELS, VOID_INELIGIBLE_MESSAGES, saleFulfillmentStatusTone } from '../lib/pos'
import { formatMoney, formatQty } from '../lib/format'
import { hasReturnableQty } from '../lib/returns'
import { useCan } from '../lib/useCan'
import { CancelDeliveryModal } from '../components/sales/CancelDeliveryModal'
import { CreateDeliveryReceiptModal, type DeliveryPrefill } from '../components/sales/CreateDeliveryReceiptModal'
import { DeliveryStatusBadge } from '../components/sales/DeliveryStatusBadge'
import { ReturnModal } from '../components/sales/ReturnModal'
import { SaleItemsTable } from '../components/sales/SaleItemsTable'
import { SaleReturnsList } from '../components/sales/SaleReturnsList'
import { StatusBadge } from '../components/sales/StatusBadge'
import { VoidSaleModal } from '../components/sales/VoidSaleModal'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { Badge, Button, ConfirmDialog, ErrorState, LoadingState, useToast } from '../components/ui'

export default function SaleDetailPage() {
  const { id = '' } = useParams()
  const { toast } = useToast()
  const canRefund = useCan('sales:return')
  const canVoidCapability = useCan('sales:void')
  const canCancelDelivery = useCan('delivery:cancel')
  const [returnOpen, setReturnOpen] = useState(false)
  const [voidOpen, setVoidOpen] = useState(false)
  const [createDeliveryOpen, setCreateDeliveryOpen] = useState(false)
  const [reschedulePrefill, setReschedulePrefill] = useState<DeliveryPrefill | null>(null)
  const [cancelTarget, setCancelTarget] = useState<{ id: string; sequenceNumber: number } | null>(null)
  const [deliverTarget, setDeliverTarget] = useState<{ id: string; sequenceNumber: number } | null>(null)

  const query = useQuery({
    queryKey: ['sales', id],
    queryFn: () => salesApi.get(id),
    enabled: !!id,
  })
  const branchesQuery = useQuery({
    queryKey: ['branches', 'sales-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const deliverySummaryQuery = useQuery({
    queryKey: ['sales', id, 'delivery-summary'],
    queryFn: () => deliveryReceiptsApi.getSaleSummary(id),
    enabled: !!id,
  })

  const markDeliveredMutation = useMutation({
    mutationFn: (deliveryReceiptId: string) => deliveryReceiptsApi.markDelivered(deliveryReceiptId),
    onSuccess: () => {
      deliverySummaryQuery.refetch()
      setDeliverTarget(null)
    },
    onError: (err) => {
      // Mirrors CancelDeliveryModal's handling of the same conflict — this delivery has a
      // RowVersion, so a concurrent status change (another tab/user) is a real, expected outcome,
      // not an unmapped error. This mutation drives a bare ConfirmDialog (no inline error slot of
      // its own), so it reports through the page's toast mechanism instead. Either way, close the
      // dialog and refetch so the UI reflects the current (possibly-changed-by-someone-else) state
      // rather than leaving the confirm dialog open with no explanation.
      if (err instanceof ApiError && err.code === 'DELIVERY_RECEIPT_CONCURRENCY_CONFLICT') {
        toast('error', 'This delivery was changed by someone else. Please refresh and try again.')
      } else {
        toast('error', err instanceof ApiError ? err.message : 'Could not mark this delivery as delivered.')
      }
      deliverySummaryQuery.refetch()
      setDeliverTarget(null)
    },
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
                    {d.deliveryCharge > 0 && (
                      <div className="flex justify-between text-text-secondary">
                        <dt>Delivery charge</dt>
                        <dd>{formatMoney(d.deliveryCharge)}</dd>
                      </div>
                    )}
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

                {deliverySummaryQuery.data && deliverySummaryQuery.data.fulfillmentStatus !== 'NotApplicable' && (
                  <div className="space-y-3">
                    <div className="flex items-center justify-between">
                      <h2 className="text-lg font-bold text-text-primary">Delivery fulfillment</h2>
                      <Badge tone={saleFulfillmentStatusTone(deliverySummaryQuery.data.fulfillmentStatus)}>
                        {SALE_FULFILLMENT_STATUS_LABELS[deliverySummaryQuery.data.fulfillmentStatus]}
                      </Badge>
                    </div>

                    <div className="overflow-x-auto rounded-xl border border-border bg-surface">
                      <table className="w-full text-left text-[13px]">
                        <thead className="border-b border-border text-text-muted">
                          <tr>
                            <th className="px-3 py-2 font-medium">Item</th>
                            <th className="px-3 py-2 text-right font-medium">Sold</th>
                            <th className="px-3 py-2 text-right font-medium">Take-now</th>
                            <th className="px-3 py-2 text-right font-medium">Required</th>
                            <th className="px-3 py-2 text-right font-medium">Pending</th>
                            <th className="px-3 py-2 text-right font-medium">Delivered</th>
                            <th className="px-3 py-2 text-right font-medium">Available</th>
                          </tr>
                        </thead>
                        <tbody>
                          {deliverySummaryQuery.data.items.map((i) => (
                            <tr key={i.saleItemId} className="border-b border-border-light last:border-0">
                              <td className="px-3 py-2 text-text-primary">
                                {i.productName}
                                {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
                              </td>
                              <td className="px-3 py-2 text-right">{formatQty(i.quantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.takeNowQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.deliveryRequiredQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.pendingQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.deliveredQuantity)}</td>
                              <td className="px-3 py-2 text-right">{formatQty(i.availableToScheduleQuantity)}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>

                    {deliverySummaryQuery.data.canCreateDelivery ? (
                      <Button
                        variant="secondary"
                        size="sm"
                        onClick={() => {
                          setReschedulePrefill(null)
                          setCreateDeliveryOpen(true)
                        }}
                      >
                        Create delivery
                      </Button>
                    ) : (
                      <p className="text-[13px] text-text-muted">
                        All delivery items have already been scheduled or delivered.
                      </p>
                    )}

                    <div className="space-y-2">
                      {deliverySummaryQuery.data.deliveries.map((dr) => (
                        <div key={dr.id} className="rounded-xl border border-border bg-surface p-3">
                          <div className="flex flex-wrap items-center justify-between gap-2">
                            <div className="flex items-center gap-2">
                              <span className="text-sm font-semibold text-text-primary">Delivery {dr.sequenceNumber}</span>
                              <DeliveryStatusBadge status={dr.status} />
                              <span className="text-[12px] text-text-muted">
                                {new Date(dr.scheduledDeliveryDate).toLocaleDateString()}
                              </span>
                            </div>
                            <div className="flex gap-2">
                              <Button
                                variant="secondary"
                                size="sm"
                                onClick={() => window.open(`/delivery-receipts/${dr.id}?print=1`, '_blank', 'noopener')}
                              >
                                View
                              </Button>
                              {dr.status === 'Pending' && (
                                <Button size="sm" onClick={() => setDeliverTarget({ id: dr.id, sequenceNumber: dr.sequenceNumber })}>
                                  Mark delivered
                                </Button>
                              )}
                              {dr.status === 'Pending' && canCancelDelivery && (
                                <Button
                                  variant="destructive"
                                  size="sm"
                                  onClick={() => setCancelTarget({ id: dr.id, sequenceNumber: dr.sequenceNumber })}
                                >
                                  Cancel
                                </Button>
                              )}
                              {dr.status === 'Cancelled' && deliverySummaryQuery.data!.canCreateDelivery && (
                                <Button
                                  variant="secondary"
                                  size="sm"
                                  onClick={() => {
                                    setReschedulePrefill({
                                      recipientName: dr.recipientName,
                                      deliveryAddress: dr.deliveryAddress,
                                      contactNumber: dr.contactNumber ?? '',
                                      deliveryNotes: dr.deliveryNotes ?? '',
                                      itemQuantities: Object.fromEntries(dr.items.map((i) => [i.saleItemId, i.quantity])),
                                    })
                                    setCreateDeliveryOpen(true)
                                  }}
                                >
                                  Schedule again
                                </Button>
                              )}
                            </div>
                          </div>
                          <p className="mt-1 text-[13px] text-text-secondary">
                            {dr.recipientName} · {dr.deliveryAddress}
                          </p>
                          {dr.status === 'Cancelled' && dr.cancellationReason && (
                            <p className="mt-1 text-[12px] text-text-muted">Cancelled: {dr.cancellationReason}</p>
                          )}
                        </div>
                      ))}
                    </div>
                  </div>
                )}

                <ReturnModal open={returnOpen} onClose={() => setReturnOpen(false)} sale={d} />
                <CreateDeliveryReceiptModal
                  open={createDeliveryOpen}
                  onClose={() => setCreateDeliveryOpen(false)}
                  saleId={d.sale.id}
                  availableItems={(deliverySummaryQuery.data?.items ?? []).filter((i) => i.availableToScheduleQuantity > 0)}
                  prefill={reschedulePrefill ?? undefined}
                />
                {cancelTarget && (
                  <CancelDeliveryModal
                    open
                    onClose={() => setCancelTarget(null)}
                    saleId={d.sale.id}
                    deliveryReceiptId={cancelTarget.id}
                    sequenceNumber={cancelTarget.sequenceNumber}
                  />
                )}
                <ConfirmDialog
                  open={deliverTarget != null}
                  onClose={() => setDeliverTarget(null)}
                  onConfirm={() => deliverTarget && markDeliveredMutation.mutate(deliverTarget.id)}
                  title={`Mark Delivery ${deliverTarget?.sequenceNumber ?? ''} as delivered?`}
                  message="This completes every item on this delivery. It cannot be undone from here — a mistaken delivery would need to be corrected as a fresh workflow, not reopened."
                  confirmLabel="Mark delivered"
                  loading={markDeliveredMutation.isPending}
                />
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
