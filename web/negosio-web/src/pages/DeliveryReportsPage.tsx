import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { Banknote, Gift, PackageCheck, Percent, Truck } from 'lucide-react'
import { branchesApi } from '../api/branches'
import { reportsApi } from '../api/reports'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { formatDeliveryCharge, formatMoney } from '../lib/format'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
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

type Filters = {
  from: string | undefined
  to: string | undefined
  branchId: string | undefined
}

const DEFAULT_FILTERS: Filters = { from: undefined, to: undefined, branchId: undefined }

export default function DeliveryReportsPage() {
  const q = usePagedQuery<Filters>({ defaultFilters: DEFAULT_FILTERS })

  const branchesQuery = useQuery({
    queryKey: ['branches', 'delivery-reports-filter'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })
  const branches = branchesQuery.data ?? []
  const multiBranch = branches.length > 1

  const query = useQuery({
    queryKey: [
      'reports',
      'deliveries',
      { page: q.page, search: q.search, from: q.filters.from, to: q.filters.to, branchId: q.filters.branchId },
    ],
    queryFn: () =>
      reportsApi.deliveries({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        branchId: q.filters.branchId,
        fromUtc: q.filters.from ? new Date(`${q.filters.from}T00:00:00.000`).toISOString() : undefined,
        toUtc: q.filters.to ? new Date(`${q.filters.to}T23:59:59.999`).toISOString() : undefined,
      }),
  })

  const filtered = Boolean(q.search || q.filters.from || q.filters.to || q.filters.branchId)
  const totals = query.data?.totals

  const header = (
    <Table.Head>
      <Table.HeaderCell>Date</Table.HeaderCell>
      <Table.HeaderCell>Sale #</Table.HeaderCell>
      <Table.HeaderCell>Recipient</Table.HeaderCell>
      <Table.HeaderCell>Address</Table.HeaderCell>
      <Table.HeaderCell align="right">Delivery charge</Table.HeaderCell>
      <Table.HeaderCell align="right">Sale total</Table.HeaderCell>
      <Table.HeaderCell>Payment</Table.HeaderCell>
      <Table.HeaderCell>Prepared by</Table.HeaderCell>
    </Table.Head>
  )
  const colCount = 8

  return (
    <DashboardLayout title="Delivery Reports">
      <div className="space-y-5">
        <h1 className="text-2xl font-bold text-text-primary">Delivery Reports</h1>

        <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-5">
          {query.isPending ? (
            Array.from({ length: 5 }).map((_, i) => <SkeletonCard key={i} />)
          ) : (
            <>
              <MetricCard icon={Truck} accent="blue" label="Total deliveries" value={totals?.totalDeliveries ?? 0} />
              <MetricCard icon={Gift} accent="green" label="Free deliveries" value={totals?.freeDeliveries ?? 0} />
              <MetricCard icon={PackageCheck} accent="purple" label="Charged deliveries" value={totals?.chargedDeliveries ?? 0} />
              <MetricCard
                icon={Banknote}
                accent="amber"
                label="Total charges collected"
                value={formatMoney(totals?.totalDeliveryCharges ?? 0)}
              />
              <MetricCard
                icon={Percent}
                accent="red"
                label="Average delivery charge"
                value={formatMoney(totals?.averageDeliveryCharge ?? 0)}
              />
            </>
          )}
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search by sale #, recipient, address, or contact number"
          />
          {multiBranch && (
            <Select
              aria-label="Branch"
              className="sm:max-w-[12rem]"
              value={q.filters.branchId ?? ''}
              onChange={(e) => q.setFilter('branchId', e.target.value || undefined)}
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
          <input
            type="date"
            aria-label="From date"
            value={q.filters.from ?? ''}
            onChange={(e) => q.setFilter('from', e.target.value || undefined)}
            className="h-11 rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
          <input
            type="date"
            aria-label="To date"
            value={q.filters.to ?? ''}
            onChange={(e) => q.setFilter('to', e.target.value || undefined)}
            className="h-11 rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            {header}
            <Table.Body>
              {Array.from({ length: 6 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: colCount }).map((__, j) => (
                    <Table.Cell key={j}>
                      <SkeletonText className={j === 0 ? 'w-24' : 'w-20'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.page.items.length === 0 ? (
          <EmptyState
            icon={Truck}
            title={filtered ? 'No deliveries match these filters' : 'No deliveries yet'}
            description={
              filtered
                ? 'Try clearing the search or date range.'
                : 'Sales marked "For delivery" in the POS will show here.'
            }
          />
        ) : (
          <>
            <Table>
              {header}
              <Table.Body>
                {query.data.page.items.map((r) => (
                  <Table.Row key={r.deliveryReceiptId}>
                    <Table.Cell>{new Date(r.createdAtUtc).toLocaleString()}</Table.Cell>
                    <Table.Cell>
                      <Link to={`/sales/${r.saleId}`} className="font-semibold text-primary-700 hover:underline">
                        #{r.saleNumber}
                      </Link>
                    </Table.Cell>
                    <Table.Cell>{r.recipientName}</Table.Cell>
                    <Table.Cell className="max-w-xs truncate">{r.deliveryAddress}</Table.Cell>
                    <Table.Cell align="right">{formatDeliveryCharge(r.deliveryCharge)}</Table.Cell>
                    <Table.Cell align="right" className="font-semibold text-text-primary">
                      {formatMoney(r.saleGrandTotal)}
                    </Table.Cell>
                    <Table.Cell>{r.paymentSummary}</Table.Cell>
                    <Table.Cell>{r.preparedByName}</Table.Cell>
                  </Table.Row>
                ))}
              </Table.Body>
            </Table>
            <Pagination
              page={query.data.page.page}
              pageSize={query.data.page.pageSize}
              totalCount={query.data.page.totalCount}
              totalPages={query.data.page.totalPages}
              onPageChange={q.setPage}
            />
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
