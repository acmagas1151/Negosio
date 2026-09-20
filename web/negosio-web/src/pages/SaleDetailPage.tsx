import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { ArrowLeft } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { ApiError } from '../api/client'
import { fulfillmentApi } from '../api/fulfillment'
import { salesApi } from '../api/pos'
import type { FulfillmentScheduleDto } from '../api/types'
import {
  CANCELLATION_DISPOSITION_LABELS,
  FULFILLMENT_METHOD_LABELS,
  PAYMENT_METHOD_LABELS,
  SALE_FULFILLMENT_STATUS_LABELS,
  VOID_INELIGIBLE_MESSAGES,
  saleFulfillmentStatusTone,
} from '../lib/pos'
import { formatMoney } from '../lib/format'
import { hasReturnableQty } from '../lib/returns'
import { useCan } from '../lib/useCan'
import { CancelFulfillmentModal } from '../components/sales/CancelFulfillmentModal'
import { ConversionHistoryList } from '../components/sales/ConversionHistoryList'
import { FulfillmentStatusBadge } from '../components/sales/FulfillmentStatusBadge'
import { ReturnModal } from '../components/sales/ReturnModal'
import { SaleItemsTable } from '../components/sales/SaleItemsTable'
import { SaleReturnsList } from '../components/sales/SaleReturnsList'
import { StatusBadge } from '../components/sales/StatusBadge'
import { VoidSaleModal } from '../components/sales/VoidSaleModal'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { Badge, Button, ConfirmDialog, ErrorState, LoadingState, useToast } from '../components/ui'

/** One row in the sale's Delivery/Pickup schedule history. Only the sale's single *active* schedule
 * (Pending, and matching `summary.activeSchedule`) ever shows the Mark-Delivered/Mark-Claimed/Cancel
 * actions — every other row here is history (cancelled, or a stale read) and is display-only.
 * "Mark delivered" only ever appears on a Delivery row and "Mark claimed" only ever appears on a
 * Pickup row — the backend would reject the mismatch, but the UI must not offer it in the first place. */
function ScheduleRow({
  schedule,
  isActive,
  canCancel,
  onMarkDelivered,
  onMarkClaimed,
  onCancel,
}: {
  schedule: FulfillmentScheduleDto
  isActive: boolean
  canCancel: boolean
  /** Only invoked for a Delivery row — see the render guard below. */
  onMarkDelivered?: () => void
  /** Only invoked for a Pickup row — see the render guard below. */
  onMarkClaimed?: () => void
  onCancel: () => void
}) {
  const showActions = isActive && schedule.status === 'Pending'
  return (
    <div className="rounded-xl border border-border bg-surface p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          <span className="text-sm font-semibold text-text-primary">
            {FULFILLMENT_METHOD_LABELS[schedule.method]} {schedule.sequenceNumber}
          </span>
          <FulfillmentStatusBadge method={schedule.method} status={schedule.status} />
          <span className="text-[12px] text-text-muted">{new Date(schedule.scheduledDate).toLocaleDateString()}</span>
        </div>
        <div className="flex gap-2">
          <Button
            variant="secondary"
            size="sm"
            onClick={() => window.open(`/delivery-receipts/${schedule.id}?print=1`, '_blank', 'noopener')}
          >
            Print
          </Button>
          {showActions && schedule.method === 'Delivery' && (
            <Button size="sm" onClick={onMarkDelivered}>
              Mark delivered
            </Button>
          )}
          {showActions && schedule.method === 'Pickup' && (
            <Button size="sm" onClick={onMarkClaimed}>
              Mark claimed
            </Button>
          )}
          {showActions && canCancel && (
            <Button variant="destructive" size="sm" onClick={onCancel}>
              Cancel
            </Button>
          )}
        </div>
      </div>
      <dl className="mt-2 space-y-1 text-[13px] text-text-secondary">
        <div>
          <dt className="inline font-medium text-text-primary">Recipient: </dt>
          <dd className="inline">{schedule.recipientName}</dd>
        </div>
        {schedule.method === 'Delivery' && schedule.deliveryAddress && (
          <div>
            <dt className="inline font-medium text-text-primary">Address: </dt>
            <dd className="inline">{schedule.deliveryAddress}</dd>
          </div>
        )}
        {schedule.contactNumber && (
          <div>
            <dt className="inline font-medium text-text-primary">Contact: </dt>
            <dd className="inline">{schedule.contactNumber}</dd>
          </div>
        )}
      </dl>
      {schedule.notes && (
        <div className="mt-2 rounded-lg border border-border-light bg-surface-subtle p-2">
          <p className="text-[12px] font-semibold text-text-primary">Notes</p>
          <p className="text-[13px] text-text-secondary">{schedule.notes}</p>
        </div>
      )}
      {schedule.status === 'Cancelled' && (
        <p className="mt-2 text-[12px] text-text-muted">
          Cancelled: {schedule.cancellationReason} ·{' '}
          {schedule.cancellationDisposition
            ? CANCELLATION_DISPOSITION_LABELS[schedule.cancellationDisposition]
            : '—'}
        </p>
      )}
    </div>
  )
}

export default function SaleDetailPage() {
  const { id = '' } = useParams()
  const { toast } = useToast()
  const qc = useQueryClient()
  const canRefund = useCan('sales:return')
  const canVoidCapability = useCan('sales:void')
  // Mirrors AuthorizationPolicies.FulfillmentCancel (Owner/Admin/Manager) on the backend. The
  // capability key itself is still named 'delivery:cancel' in useCan.ts — it predates the backend's
  // DeliveryCancel -> FulfillmentCancel rename and covers the same role set for both Delivery and
  // Pickup cancellation. Renaming the key is out of this task's file list (useCan.ts isn't touched
  // here); flagged for whoever next edits useCan.ts (likely Task 16, which owns the cancel modal).
  const canCancelFulfillment = useCan('delivery:cancel')
  const [returnOpen, setReturnOpen] = useState(false)
  const [voidOpen, setVoidOpen] = useState(false)
  const [cancelTarget, setCancelTarget] = useState<FulfillmentScheduleDto | null>(null)
  const [deliverTarget, setDeliverTarget] = useState<{ id: string; sequenceNumber: number } | null>(null)
  const [claimTarget, setClaimTarget] = useState<{ id: string; sequenceNumber: number } | null>(null)

  const query = useQuery({
    queryKey: ['sales', id],
    queryFn: () => salesApi.get(id),
    enabled: !!id,
  })
  const branchesQuery = useQuery({
    queryKey: ['branches', 'sales-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const fulfillmentSummaryQuery = useQuery({
    queryKey: ['sales', id, 'fulfillment-summary'],
    queryFn: () => fulfillmentApi.getSaleSummary(id),
    enabled: !!id,
  })

  const invalidateFulfillment = () => {
    qc.invalidateQueries({ queryKey: ['sales', id] })
    qc.invalidateQueries({ queryKey: ['sales', id, 'fulfillment-summary'] })
  }

  const markDeliveredMutation = useMutation({
    mutationFn: (deliveryReceiptId: string) => fulfillmentApi.markDelivered(deliveryReceiptId),
    onSuccess: () => {
      invalidateFulfillment()
      setDeliverTarget(null)
    },
    onError: (err) => {
      // This schedule has a RowVersion, so a concurrent status change (another tab/user) is a real,
      // expected outcome, not an unmapped error. This mutation drives a bare ConfirmDialog (no inline
      // error slot of its own), so it reports through the page's toast mechanism instead. Either way,
      // close the dialog and refetch so the UI reflects the current (possibly-changed-by-someone-else)
      // state rather than leaving the confirm dialog open with no explanation.
      if (err instanceof ApiError && err.code === 'DELIVERY_RECEIPT_CONCURRENCY_CONFLICT') {
        toast('error', 'This delivery was changed by someone else. Please refresh and try again.')
      } else {
        toast('error', err instanceof ApiError ? err.message : 'Could not mark this delivery as delivered.')
      }
      invalidateFulfillment()
      setDeliverTarget(null)
    },
  })

  const markClaimedMutation = useMutation({
    mutationFn: (pickupId: string) => fulfillmentApi.markClaimed(pickupId),
    onSuccess: () => {
      invalidateFulfillment()
      setClaimTarget(null)
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'DELIVERY_RECEIPT_CONCURRENCY_CONFLICT') {
        toast('error', 'This pickup was changed by someone else. Please refresh and try again.')
      } else {
        toast('error', err instanceof ApiError ? err.message : 'Could not mark this pickup as claimed.')
      }
      invalidateFulfillment()
      setClaimTarget(null)
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
            const summary = fulfillmentSummaryQuery.data
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

                {summary && (
                  <div className="space-y-4">
                    <div className="flex items-center justify-between">
                      <h2 className="text-lg font-bold text-text-primary">Fulfillment</h2>
                      <Badge tone={saleFulfillmentStatusTone(summary.fulfillmentStatus)}>
                        {SALE_FULFILLMENT_STATUS_LABELS[summary.fulfillmentStatus]}
                      </Badge>
                    </div>

                    {(summary.deliveries.length > 0 || summary.pickups.length > 0) &&
                      (() => {
                        // The sale's whole-sale method: read off the active schedule when there is
                        // one, else fall back to whichever history list is non-empty (a fully
                        // cancelled sale with no replacement — see Task 3's note, shouldn't normally
                        // happen but the fallback keeps the section from silently disappearing).
                        const method: 'Delivery' | 'Pickup' =
                          summary.activeSchedule?.method === 'Pickup' || summary.activeSchedule?.method === 'Delivery'
                            ? summary.activeSchedule.method
                            : summary.deliveries.length > 0
                              ? 'Delivery'
                              : 'Pickup'
                        const schedules = method === 'Delivery' ? summary.deliveries : summary.pickups
                        // Active schedule first, then cancelled ones most-recent-first.
                        const sorted = [...schedules].sort((a, b) => {
                          const aActive = a.status !== 'Cancelled'
                          const bActive = b.status !== 'Cancelled'
                          if (aActive !== bActive) return aActive ? -1 : 1
                          return b.sequenceNumber - a.sequenceNumber
                        })
                        return (
                          <div className="space-y-2">
                            <div className="flex items-center justify-between">
                              <h3 className="text-sm font-semibold text-text-primary">
                                {FULFILLMENT_METHOD_LABELS[method]} history
                              </h3>
                              {method === 'Delivery' && summary.deliveryCharge > 0 && (
                                <span className="text-[13px] text-text-secondary">
                                  Delivery charge: {formatMoney(summary.deliveryCharge)}
                                </span>
                              )}
                            </div>
                            <div className="space-y-2">
                              {sorted.map((s) => (
                                <ScheduleRow
                                  key={s.id}
                                  schedule={s}
                                  isActive={summary.activeSchedule?.id === s.id}
                                  canCancel={canCancelFulfillment}
                                  onMarkDelivered={() => setDeliverTarget({ id: s.id, sequenceNumber: s.sequenceNumber })}
                                  onMarkClaimed={() => setClaimTarget({ id: s.id, sequenceNumber: s.sequenceNumber })}
                                  onCancel={() => setCancelTarget(s)}
                                />
                              ))}
                            </div>
                          </div>
                        )
                      })()}

                    <ConversionHistoryList conversions={summary.conversions} />
                  </div>
                )}

                <ReturnModal open={returnOpen} onClose={() => setReturnOpen(false)} sale={d} />
                {cancelTarget && (
                  <CancelFulfillmentModal
                    key={cancelTarget.id}
                    open
                    onClose={() => setCancelTarget(null)}
                    saleId={d.sale.id}
                    schedule={cancelTarget}
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
                <ConfirmDialog
                  open={claimTarget != null}
                  onClose={() => setClaimTarget(null)}
                  onConfirm={() => claimTarget && markClaimedMutation.mutate(claimTarget.id)}
                  title={`Mark Pickup ${claimTarget?.sequenceNumber ?? ''} as claimed?`}
                  message="This completes every item on this pickup. It cannot be undone from here — a mistaken claim would need to be corrected as a fresh workflow, not reopened."
                  confirmLabel="Mark claimed"
                  loading={markClaimedMutation.isPending}
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
