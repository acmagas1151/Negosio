import { useState } from 'react'
import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import {
  Banknote,
  Gift,
  PackageCheck,
  PackageOpen,
  Percent,
  ShoppingBag,
  Truck,
} from 'lucide-react'
import { branchesApi } from '../api/branches'
import { reportsApi } from '../api/reports'
import type {
  DeliveryReportPreset,
  PickupReportPreset,
  SaleFulfillmentStatus,
} from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { formatDeliveryCharge, formatMoney, formatQty } from '../lib/format'
import {
  SALE_FULFILLMENT_STATUS_LABELS,
  saleFulfillmentStatusTone,
} from '../lib/pos'
import { FulfillmentStatusBadge } from '../components/sales/FulfillmentStatusBadge'
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

// ---- Deliveries tab (unchanged behavior — a regression surface, see task-18-brief.md) ----

const DELIVERY_PRESETS: { value: DeliveryReportPreset; label: string }[] = [
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

type DeliveryFulfillmentFilters = { branchId: string | undefined; status: SaleFulfillmentStatus | undefined }
const DELIVERY_FULFILLMENT_DEFAULT_FILTERS: DeliveryFulfillmentFilters = { branchId: undefined, status: undefined }

// ---- Pickups tab (mirrors Deliveries, minus address and delivery charge) ----

const PICKUP_PRESETS: { value: PickupReportPreset; label: string }[] = [
  { value: 'All', label: 'All' },
  { value: 'Today', label: 'Today' },
  { value: 'Upcoming', label: 'Upcoming' },
  { value: 'Overdue', label: 'Overdue' },
  { value: 'Pending', label: 'Pending' },
  { value: 'Claimed', label: 'Claimed' },
  { value: 'Cancelled', label: 'Cancelled' },
]

type PickupFilters = { branchId: string | undefined; preset: PickupReportPreset | undefined }
const PICKUP_DEFAULT_FILTERS: PickupFilters = { branchId: undefined, preset: undefined }

type Tab = 'deliveries' | 'pickups'

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md px-3 py-1.5 text-[13px] font-semibold ${active ? 'bg-white text-text-primary shadow-sm' : 'text-text-muted'}`}
    >
      {children}
    </button>
  )
}

export default function FulfillmentReportsPage() {
  const [params, setParams] = useSearchParams()
  const tab: Tab = params.get('tab') === 'pickups' ? 'pickups' : 'deliveries'
  const setTab = (next: Tab) => {
    setParams((prev) => {
      const p = new URLSearchParams(prev)
      if (next === 'deliveries') p.delete('tab')
      else p.set('tab', next)
      p.delete('page')
      return p
    })
  }

  const [deliveryView, setDeliveryView] = useState<'schedule' | 'fulfillment'>('schedule')

  const branchesQuery = useQuery({
    queryKey: ['branches', 'fulfillment-reports-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const branches = branchesQuery.data ?? []
  const multiBranch = branches.length > 1

  // --- Deliveries tab queries (unchanged from today) ---

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
    enabled: tab === 'deliveries' && deliveryView === 'schedule',
  })

  const deliveryFulfillment = usePagedQuery<DeliveryFulfillmentFilters>({
    defaultFilters: DELIVERY_FULFILLMENT_DEFAULT_FILTERS,
  })
  const deliveryFulfillmentQuery = useQuery({
    queryKey: [
      'reports',
      'delivery-fulfillment',
      {
        page: deliveryFulfillment.page,
        search: deliveryFulfillment.search,
        branchId: deliveryFulfillment.filters.branchId,
        status: deliveryFulfillment.filters.status,
      },
    ],
    queryFn: () =>
      reportsApi.deliveryFulfillment({
        page: deliveryFulfillment.page,
        pageSize: deliveryFulfillment.pageSize,
        search: deliveryFulfillment.search || undefined,
        branchId: deliveryFulfillment.filters.branchId,
        status: deliveryFulfillment.filters.status,
      }),
    enabled: tab === 'deliveries' && deliveryView === 'fulfillment',
  })

  // --- Pickups tab query ---

  const pickup = usePagedQuery<PickupFilters>({ defaultFilters: PICKUP_DEFAULT_FILTERS })
  const pickupQuery = useQuery({
    queryKey: [
      'reports',
      'pickups',
      { page: pickup.page, search: pickup.search, branchId: pickup.filters.branchId, preset: pickup.filters.preset },
    ],
    queryFn: () =>
      reportsApi.pickups({
        page: pickup.page,
        pageSize: pickup.pageSize,
        search: pickup.search || undefined,
        branchId: pickup.filters.branchId,
        preset: pickup.filters.preset,
      }),
    enabled: tab === 'pickups',
  })

  return (
    <DashboardLayout title="Fulfillment Reports">
      <div className="space-y-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-bold text-text-primary">Fulfillment Reports</h1>
          <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
            <TabButton active={tab === 'deliveries'} onClick={() => setTab('deliveries')}>
              Deliveries
            </TabButton>
            <TabButton active={tab === 'pickups'} onClick={() => setTab('pickups')}>
              Pickups
            </TabButton>
          </div>
        </div>

        {tab === 'deliveries' && (
          <div className="space-y-5">
            <div className="flex justify-end">
              <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
                <TabButton active={deliveryView === 'schedule'} onClick={() => setDeliveryView('schedule')}>
                  Delivery schedule
                </TabButton>
                <TabButton active={deliveryView === 'fulfillment'} onClick={() => setDeliveryView('fulfillment')}>
                  Sale fulfillment
                </TabButton>
              </div>
            </div>

            {deliveryView === 'schedule' ? (
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
                    {DELIVERY_PRESETS.map((p) => (
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
                          {Array.from({ length: 8 }).map((__, j) => (
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
                        <Table.HeaderCell>Notes</Table.HeaderCell>
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
                              <FulfillmentStatusBadge method="Delivery" status={r.status} />
                              {r.isOverdue && <span className="ml-1.5 text-[11px] font-semibold text-danger">Overdue</span>}
                            </Table.Cell>
                            <Table.Cell>{r.recipientName}</Table.Cell>
                            <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                            <Table.Cell>{r.preparedByName}</Table.Cell>
                            <Table.Cell>
                              {r.deliveryNotes ? (
                                <span className="block max-w-[16rem] truncate" title={r.deliveryNotes}>
                                  {r.deliveryNotes}
                                </span>
                              ) : (
                                <span className="text-text-muted">—</span>
                              )}
                            </Table.Cell>
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
                  {deliveryFulfillmentQuery.isPending ? (
                    <>
                      <SkeletonCard />
                      <SkeletonCard />
                    </>
                  ) : (
                    <>
                      <MetricCard
                        icon={PackageCheck}
                        accent="blue"
                        label="Sales with a delivery component"
                        value={deliveryFulfillmentQuery.data?.totals.totalSales ?? 0}
                      />
                      <MetricCard
                        icon={Banknote}
                        accent="amber"
                        label="Total delivery charges"
                        value={formatMoney(deliveryFulfillmentQuery.data?.totals.totalDeliveryCharges ?? 0)}
                      />
                    </>
                  )}
                </div>

                <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
                  <SearchInput
                    className="sm:max-w-xs"
                    value={deliveryFulfillment.searchInput}
                    onChange={deliveryFulfillment.setSearchInput}
                    placeholder="Search by sale #"
                  />
                  {multiBranch && (
                    <Select
                      aria-label="Branch"
                      className="sm:max-w-[12rem]"
                      value={deliveryFulfillment.filters.branchId ?? ''}
                      onChange={(e) => deliveryFulfillment.setFilter('branchId', e.target.value || undefined)}
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
                    value={deliveryFulfillment.filters.status ?? ''}
                    onChange={(e) =>
                      deliveryFulfillment.setFilter('status', (e.target.value as SaleFulfillmentStatus) || undefined)
                    }
                  >
                    <option value="">All statuses</option>
                    {(Object.keys(SALE_FULFILLMENT_STATUS_LABELS) as SaleFulfillmentStatus[])
                      .map((s) => (
                        <option key={s} value={s}>
                          {SALE_FULFILLMENT_STATUS_LABELS[s]}
                        </option>
                      ))}
                  </Select>
                </div>

                {deliveryFulfillmentQuery.isError ? (
                  <ErrorState
                    message={(deliveryFulfillmentQuery.error as Error).message}
                    onRetry={() => deliveryFulfillmentQuery.refetch()}
                  />
                ) : deliveryFulfillmentQuery.isPending ? (
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
                ) : deliveryFulfillmentQuery.data.page.items.length === 0 ? (
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
                        {deliveryFulfillmentQuery.data.page.items.map((r) => (
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
                      page={deliveryFulfillmentQuery.data.page.page}
                      pageSize={deliveryFulfillmentQuery.data.page.pageSize}
                      totalCount={deliveryFulfillmentQuery.data.page.totalCount}
                      totalPages={deliveryFulfillmentQuery.data.page.totalPages}
                      onPageChange={deliveryFulfillment.setPage}
                    />
                  </>
                )}
              </>
            )}
          </div>
        )}

        {tab === 'pickups' && (
          <div className="space-y-5">
            <div className="grid grid-cols-2 gap-3 sm:grid-cols-2 lg:grid-cols-2">
              {pickupQuery.isPending ? (
                <>
                  <SkeletonCard />
                  <SkeletonCard />
                </>
              ) : (
                <>
                  <MetricCard icon={PackageOpen} accent="blue" label="Schedules" value={pickupQuery.data?.totals.totalSchedules ?? 0} />
                  <MetricCard icon={ShoppingBag} accent="purple" label="Distinct sales" value={pickupQuery.data?.totals.distinctSalesCount ?? 0} />
                </>
              )}
            </div>

            <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
              <SearchInput
                className="sm:max-w-xs"
                value={pickup.searchInput}
                onChange={pickup.setSearchInput}
                placeholder="Search by sale #, recipient, or contact number"
              />
              {multiBranch && (
                <Select
                  aria-label="Branch"
                  className="sm:max-w-[12rem]"
                  value={pickup.filters.branchId ?? ''}
                  onChange={(e) => pickup.setFilter('branchId', e.target.value || undefined)}
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
                value={pickup.filters.preset ?? 'All'}
                onChange={(e) => pickup.setFilter('preset', (e.target.value as PickupReportPreset) || undefined)}
              >
                {PICKUP_PRESETS.map((p) => (
                  <option key={p.value} value={p.value}>
                    {p.label}
                  </option>
                ))}
              </Select>
            </div>

            {pickupQuery.isError ? (
              <ErrorState message={(pickupQuery.error as Error).message} onRetry={() => pickupQuery.refetch()} />
            ) : pickupQuery.isPending ? (
              <Table>
                <Table.Head>
                  <Table.HeaderCell>Pickup</Table.HeaderCell>
                  <Table.HeaderCell>Sale #</Table.HeaderCell>
                  <Table.HeaderCell>Scheduled</Table.HeaderCell>
                  <Table.HeaderCell>Status</Table.HeaderCell>
                  <Table.HeaderCell>Recipient</Table.HeaderCell>
                  <Table.HeaderCell>Prepared by</Table.HeaderCell>
                  <Table.HeaderCell>Notes</Table.HeaderCell>
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
            ) : pickupQuery.data.page.items.length === 0 ? (
              <EmptyState
                icon={PackageOpen}
                title="No pickups match these filters"
                description="Try clearing the search or filter."
              />
            ) : (
              <>
                <Table>
                  <Table.Head>
                    <Table.HeaderCell>Pickup</Table.HeaderCell>
                    <Table.HeaderCell>Sale #</Table.HeaderCell>
                    <Table.HeaderCell>Scheduled</Table.HeaderCell>
                    <Table.HeaderCell>Status</Table.HeaderCell>
                    <Table.HeaderCell>Recipient</Table.HeaderCell>
                    <Table.HeaderCell>Prepared by</Table.HeaderCell>
                    <Table.HeaderCell>Notes</Table.HeaderCell>
                  </Table.Head>
                  <Table.Body>
                    {pickupQuery.data.page.items.map((r) => (
                      <Table.Row key={r.deliveryReceiptId} className={r.isOverdue ? 'bg-danger-light/40' : undefined}>
                        <Table.Cell>Pickup {r.sequenceNumber}</Table.Cell>
                        <Table.Cell>
                          <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                            #{r.saleNumber}
                          </Link>
                        </Table.Cell>
                        <Table.Cell>{new Date(`${r.scheduledPickupDate}T00:00:00`).toLocaleDateString()}</Table.Cell>
                        <Table.Cell>
                          <FulfillmentStatusBadge method="Pickup" status={r.status} />
                          {r.isOverdue && <span className="ml-1.5 text-[11px] font-semibold text-danger">Overdue</span>}
                        </Table.Cell>
                        <Table.Cell>{r.recipientName}</Table.Cell>
                        <Table.Cell>{r.preparedByName}</Table.Cell>
                        <Table.Cell>
                          {r.notes ? (
                            <span className="block max-w-[16rem] truncate" title={r.notes}>
                              {r.notes}
                            </span>
                          ) : (
                            <span className="text-text-muted">—</span>
                          )}
                        </Table.Cell>
                      </Table.Row>
                    ))}
                  </Table.Body>
                </Table>
                <Pagination
                  page={pickupQuery.data.page.page}
                  pageSize={pickupQuery.data.page.pageSize}
                  totalCount={pickupQuery.data.page.totalCount}
                  totalPages={pickupQuery.data.page.totalPages}
                  onPageChange={pickup.setPage}
                />
              </>
            )}
          </div>
        )}
      </div>
    </DashboardLayout>
  )
}
