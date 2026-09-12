import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Banknote, Gift, PackageCheck, Percent, Truck } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { reportsApi } from '../api/reports'
import type { DeliveryReportPreset, SaleFulfillmentStatus } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { formatDeliveryCharge, formatMoney, formatQty } from '../lib/format'
import { SALE_FULFILLMENT_STATUS_LABELS, saleFulfillmentStatusTone } from '../lib/pos'
import { DeliveryStatusBadge } from '../components/sales/DeliveryStatusBadge'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  EmptyState,
  ErrorState,
  MetricCard,
  Pagination,
  SearchInput,
  Select,
  SkeletonCard,
  SkeletonText,
  Table,
} from '../components/ui'

const PRESETS: { value: DeliveryReportPreset; label: string }[] = [
  { value: 'All', label: 'All' },
  { value: 'Today', label: 'Today' },
  { value: 'Upcoming', label: 'Upcoming' },
  { value: 'Overdue', label: 'Overdue' },
  { value: 'Delivered', label: 'Delivered' },
  { value: 'Cancelled', label: 'Cancelled' },
  { value: 'NeedsRescheduling', label: 'Needs rescheduling' },
]

type ScheduleFilters = { branchId: string | undefined; preset: DeliveryReportPreset | undefined }
const SCHEDULE_DEFAULT_FILTERS: ScheduleFilters = { branchId: undefined, preset: undefined }

type FulfillmentFilters = { branchId: string | undefined; status: SaleFulfillmentStatus | undefined }
const FULFILLMENT_DEFAULT_FILTERS: FulfillmentFilters = { branchId: undefined, status: undefined }

export default function DeliveryReportsPage() {
  const [view, setView] = useState<'schedule' | 'fulfillment'>('schedule')

  const branchesQuery = useQuery({
    queryKey: ['branches', 'delivery-reports-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const branches = branchesQuery.data ?? []
  const multiBranch = branches.length > 1

  const schedule = usePagedQuery<ScheduleFilters>({ defaultFilters: SCHEDULE_DEFAULT_FILTERS })
  const scheduleQuery = useQuery({
    queryKey: [
      'reports',
      'deliveries',
      { page: schedule.page, search: schedule.search, branchId: schedule.filters.branchId, preset: schedule.filters.preset },
    ],
    queryFn: () =>
      reportsApi.deliveries({
        page: schedule.page,
        pageSize: schedule.pageSize,
        search: schedule.search || undefined,
        branchId: schedule.filters.branchId,
        preset: schedule.filters.preset,
      }),
    enabled: view === 'schedule',
  })

  const fulfillment = usePagedQuery<FulfillmentFilters>({ defaultFilters: FULFILLMENT_DEFAULT_FILTERS })
  const fulfillmentQuery = useQuery({
    queryKey: [
      'reports',
      'delivery-fulfillment',
      { page: fulfillment.page, search: fulfillment.search, branchId: fulfillment.filters.branchId, status: fulfillment.filters.status },
    ],
    queryFn: () =>
      reportsApi.deliveryFulfillment({
        page: fulfillment.page,
        pageSize: fulfillment.pageSize,
        search: fulfillment.search || undefined,
        branchId: fulfillment.filters.branchId,
        status: fulfillment.filters.status,
      }),
    enabled: view === 'fulfillment',
  })

  return (
    <DashboardLayout title="Delivery Reports">
      <div className="space-y-5">
        <div className="flex items-center justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Delivery Reports</h1>
          <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
            <button
              type="button"
              onClick={() => setView('schedule')}
              className={`rounded-md px-3 py-1.5 text-[13px] font-semibold ${view === 'schedule' ? 'bg-white text-text-primary shadow-sm' : 'text-text-muted'}`}
            >
              Delivery schedule
            </button>
            <button
              type="button"
              onClick={() => setView('fulfillment')}
              className={`rounded-md px-3 py-1.5 text-[13px] font-semibold ${view === 'fulfillment' ? 'bg-white text-text-primary shadow-sm' : 'text-text-muted'}`}
            >
              Sale fulfillment
            </button>
          </div>
        </div>

        {view === 'schedule' ? (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
              {scheduleQuery.isPending ? (
                Array.from({ length: 5 }).map((_, i) => <SkeletonCard key={i} />)
              ) : (
                <>
                  <MetricCard icon={Truck} accent="blue" label="Schedules" value={scheduleQuery.data?.totals.totalSchedules ?? 0} />
                  <MetricCard icon={PackageCheck} accent="purple" label="Distinct sales" value={scheduleQuery.data?.totals.distinctSalesCount ?? 0} />
                  <MetricCard icon={Gift} accent="green" label="Free-delivery sales" value={scheduleQuery.data?.totals.freeDeliverySalesCount ?? 0} />
                  <MetricCard
                    icon={Banknote}
                    accent="amber"
                    label="Total charges collected"
                    value={formatMoney(scheduleQuery.data?.totals.totalDeliveryCharges ?? 0)}
                  />
                  <MetricCard
                    icon={Percent}
                    accent="red"
                    label="Average charge / sale"
                    value={formatMoney(scheduleQuery.data?.totals.averageDeliveryChargePerSale ?? 0)}
                  />
                </>
              )}
            </div>

            <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
              <SearchInput
                className="sm:max-w-xs"
                value={schedule.searchInput}
                onChange={schedule.setSearchInput}
                placeholder="Search by sale #, recipient, address, or contact number"
              />
              {multiBranch && (
                <Select
                  aria-label="Branch"
                  className="sm:max-w-[12rem]"
                  value={schedule.filters.branchId ?? ''}
                  onChange={(e) => schedule.setFilter('branchId', e.target.value || undefined)}
                >
                  <option value="">All branches</option>
                  {branches.map((b) => (
                    <option key={b.id} value={b.id}>
                      {b.name}
                      {b.isActive ? '' : ' (inactive)'}
                    </option>
                  ))}
                </Select>
              )}
              <Select
                aria-label="Filter"
                className="sm:max-w-[12rem]"
                value={schedule.filters.preset ?? 'All'}
                onChange={(e) => schedule.setFilter('preset', (e.target.value as DeliveryReportPreset) || undefined)}
              >
                {PRESETS.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </Select>
            </div>

            {scheduleQuery.isError ? (
              <ErrorState message={(scheduleQuery.error as Error).message} onRetry={() => scheduleQuery.refetch()} />
            ) : scheduleQuery.isPending ? (
              <Table>
                <Table.Head>
                  <Table.HeaderCell>Delivery</Table.HeaderCell>
                  <Table.HeaderCell>Sale #</Table.HeaderCell>
                  <Table.HeaderCell>Scheduled</Table.HeaderCell>
                  <Table.HeaderCell>Status</Table.HeaderCell>
                  <Table.HeaderCell>Recipient</Table.HeaderCell>
                  <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                  <Table.HeaderCell>Prepared by</Table.HeaderCell>
                </Table.Head>
                <Table.Body>
                  {Array.from({ length: 6 }).map((_, i) => (
                    <Table.Row key={i}>
                      {Array.from({ length: 7 }).map((__, j) => (
                        <Table.Cell key={j}>
                          <SkeletonText className={j === 0 ? 'w-24' : 'w-20'} />
                        </Table.Cell>
                      ))}
                    </Table.Row>
                  ))}
                </Table.Body>
              </Table>
            ) : scheduleQuery.data.page.items.length === 0 ? (
              <EmptyState
                icon={Truck}
                title="No deliveries match these filters"
                description="Try clearing the search or filter."
              />
            ) : (
              <>
                <Table>
                  <Table.Head>
                    <Table.HeaderCell>Delivery</Table.HeaderCell>
                    <Table.HeaderCell>Sale #</Table.HeaderCell>
                    <Table.HeaderCell>Scheduled</Table.HeaderCell>
                    <Table.HeaderCell>Status</Table.HeaderCell>
                    <Table.HeaderCell>Recipient</Table.HeaderCell>
                    <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                    <Table.HeaderCell>Prepared by</Table.HeaderCell>
                  </Table.Head>
                  <Table.Body>
                    {scheduleQuery.data.page.items.map((r) => (
                      <Table.Row key={r.deliveryReceiptId} className={r.isOverdue ? 'bg-danger-light/40' : undefined}>
                        <Table.Cell>Delivery {r.sequenceNumber}</Table.Cell>
                        <Table.Cell>
                          <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                            #{r.saleNumber}
                          </Link>
                        </Table.Cell>
                        <Table.Cell>{new Date(r.scheduledDeliveryDate).toLocaleDateString()}</Table.Cell>
                        <Table.Cell>
                          <DeliveryStatusBadge status={r.status} />
                          {r.isOverdue && <span className="ml-1.5 text-[11px] font-semibold text-danger">Overdue</span>}
                        </Table.Cell>
                        <Table.Cell>{r.recipientName}</Table.Cell>
                        <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                        <Table.Cell>{r.preparedByName}</Table.Cell>
                      </Table.Row>
                    ))}
                  </Table.Body>
                </Table>
                <Pagination
                  page={scheduleQuery.data.page.page}
                  pageSize={scheduleQuery.data.page.pageSize}
                  totalCount={scheduleQuery.data.page.totalCount}
                  totalPages={scheduleQuery.data.page.totalPages}
                  onPageChange={schedule.setPage}
                />
              </>
            )}
          </>
        ) : (
          <>
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-2 lg:grid-cols-2">
              {fulfillmentQuery.isPending ? (
                <>
                  <SkeletonCard />
                  <SkeletonCard />
                </>
              ) : (
                <>
                  <MetricCard icon={PackageCheck} accent="blue" label="Sales with a delivery component" value={fulfillmentQuery.data?.totals.totalSales ?? 0} />
                  <MetricCard icon={Banknote} accent="amber" label="Total delivery charges" value={formatMoney(fulfillmentQuery.data?.totals.totalDeliveryCharges ?? 0)} />
                </>
              )}
            </div>

            <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
              <SearchInput
                className="sm:max-w-xs"
                value={fulfillment.searchInput}
                onChange={fulfillment.setSearchInput}
                placeholder="Search by sale #"
              />
              {multiBranch && (
                <Select
                  aria-label="Branch"
                  className="sm:max-w-[12rem]"
                  value={fulfillment.filters.branchId ?? ''}
                  onChange={(e) => fulfillment.setFilter('branchId', e.target.value || undefined)}
                >
                  <option value="">All branches</option>
                  {branches.map((b) => (
                    <option key={b.id} value={b.id}>
                      {b.name}
                      {b.isActive ? '' : ' (inactive)'}
                    </option>
                  ))}
                </Select>
              )}
              <Select
                aria-label="Fulfillment status"
                className="sm:max-w-[14rem]"
                value={fulfillment.filters.status ?? ''}
                onChange={(e) => fulfillment.setFilter('status', (e.target.value as SaleFulfillmentStatus) || undefined)}
              >
                <option value="">All statuses</option>
                {(Object.keys(SALE_FULFILLMENT_STATUS_LABELS) as SaleFulfillmentStatus[])
                  .filter((s) => s !== 'NotApplicable')
                  .map((s) => (
                    <option key={s} value={s}>
                      {SALE_FULFILLMENT_STATUS_LABELS[s]}
                    </option>
                  ))}
              </Select>
            </div>

            {fulfillmentQuery.isError ? (
              <ErrorState message={(fulfillmentQuery.error as Error).message} onRetry={() => fulfillmentQuery.refetch()} />
            ) : fulfillmentQuery.isPending ? (
              <Table>
                <Table.Head>
                  <Table.HeaderCell>Sale #</Table.HeaderCell>
                  <Table.HeaderCell>Created</Table.HeaderCell>
                  <Table.HeaderCell>Status</Table.HeaderCell>
                  <Table.HeaderCell align="right">Required</Table.HeaderCell>
                  <Table.HeaderCell align="right">Pending</Table.HeaderCell>
                  <Table.HeaderCell align="right">Delivered</Table.HeaderCell>
                  <Table.HeaderCell align="right">Unscheduled</Table.HeaderCell>
                  <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                </Table.Head>
                <Table.Body>
                  {Array.from({ length: 6 }).map((_, i) => (
                    <Table.Row key={i}>
                      {Array.from({ length: 8 }).map((__, j) => (
                        <Table.Cell key={j}>
                          <SkeletonText className={j === 0 ? 'w-24' : 'w-16'} />
                        </Table.Cell>
                      ))}
                    </Table.Row>
                  ))}
                </Table.Body>
              </Table>
            ) : fulfillmentQuery.data.page.items.length === 0 ? (
              <EmptyState
                icon={PackageCheck}
                title="No sales match these filters"
                description="Sales with at least one item marked for delivery will show here."
              />
            ) : (
              <>
                <Table>
                  <Table.Head>
                    <Table.HeaderCell>Sale #</Table.HeaderCell>
                    <Table.HeaderCell>Created</Table.HeaderCell>
                    <Table.HeaderCell>Status</Table.HeaderCell>
                    <Table.HeaderCell align="right">Required</Table.HeaderCell>
                    <Table.HeaderCell align="right">Pending</Table.HeaderCell>
                    <Table.HeaderCell align="right">Delivered</Table.HeaderCell>
                    <Table.HeaderCell align="right">Unscheduled</Table.HeaderCell>
                    <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
                  </Table.Head>
                  <Table.Body>
                    {fulfillmentQuery.data.page.items.map((r) => (
                      <Table.Row key={r.saleId}>
                        <Table.Cell>
                          <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                            #{r.saleNumber}
                          </Link>
                        </Table.Cell>
                        <Table.Cell>{new Date(r.saleCreatedAtUtc).toLocaleDateString()}</Table.Cell>
                        <Table.Cell>
                          <Badge tone={saleFulfillmentStatusTone(r.fulfillmentStatus)}>
                            {SALE_FULFILLMENT_STATUS_LABELS[r.fulfillmentStatus]}
                          </Badge>
                        </Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalDeliveryRequiredQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalPendingQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalDeliveredQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatQty(r.totalUnscheduledQuantity)}</Table.Cell>
                        <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                      </Table.Row>
                    ))}
                  </Table.Body>
                </Table>
                <Pagination
                  page={fulfillmentQuery.data.page.page}
                  pageSize={fulfillmentQuery.data.page.pageSize}
                  totalCount={fulfillmentQuery.data.page.totalCount}
                  totalPages={fulfillmentQuery.data.page.totalPages}
                  onPageChange={fulfillment.setPage}
                />
              </>
            )}
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
