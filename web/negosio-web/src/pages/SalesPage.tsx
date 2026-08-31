import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ClipboardList, ReceiptText } from 'lucide-react'
import { salesApi } from '../api/pos'
import type { SaleStatus } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { SALE_STATUS_LABELS } from '../lib/pos'
import { formatMoney } from '../lib/format'
import { StatusBadge } from '../components/sales/StatusBadge'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  EmptyState,
  ErrorState,
  Pagination,
  SearchInput,
  Select,
  SkeletonText,
  Table,
} from '../components/ui'

type Filters = { status: string | undefined; from: string | undefined; to: string | undefined }

const DEFAULT_FILTERS: Filters = { status: undefined, from: undefined, to: undefined }

const STATUSES: SaleStatus[] = ['Completed', 'PartiallyRefunded', 'Refunded', 'Voided']

export default function SalesPage() {
  const q = usePagedQuery<Filters>({ defaultFilters: DEFAULT_FILTERS })

  const query = useQuery({
    queryKey: [
      'sales',
      { page: q.page, search: q.search, status: q.filters.status, from: q.filters.from, to: q.filters.to },
    ],
    queryFn: () =>
      salesApi.list({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        status: (q.filters.status as SaleStatus) || undefined,
        fromUtc: q.filters.from ? new Date(`${q.filters.from}T00:00:00`).toISOString() : undefined,
        toUtc: q.filters.to ? new Date(`${q.filters.to}T23:59:59`).toISOString() : undefined,
      }),
  })

  const filtered = Boolean(q.search || q.filters.status || q.filters.from || q.filters.to)

  const header = (
    <Table.Head>
      <Table.HeaderCell>Sale #</Table.HeaderCell>
      <Table.HeaderCell>Date</Table.HeaderCell>
      <Table.HeaderCell align="right">Items</Table.HeaderCell>
      <Table.HeaderCell align="right">Total</Table.HeaderCell>
      <Table.HeaderCell>Payment</Table.HeaderCell>
      <Table.HeaderCell>Status</Table.HeaderCell>
    </Table.Head>
  )

  return (
    <DashboardLayout title="Sales">
      <div className="space-y-5">
        <h1 className="text-2xl font-bold text-text-primary">Sales</h1>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search by sale number"
          />
          <Select
            aria-label="Status"
            className="sm:max-w-[12rem]"
            value={q.filters.status ?? ''}
            onChange={(e) => q.setFilter('status', e.target.value || undefined)}
          >
            <option value="">All statuses</option>
            {STATUSES.map((s) => (
              <option key={s} value={s}>
                {SALE_STATUS_LABELS[s]}
              </option>
            ))}
          </Select>
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
                  {Array.from({ length: 6 }).map((__, j) => (
                    <Table.Cell key={j}>
                      <SkeletonText className={j === 0 ? 'w-32' : 'w-16'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={filtered ? ReceiptText : ClipboardList}
            title={filtered ? 'No sales match these filters' : 'No sales yet'}
            description={
              filtered
                ? 'Try clearing the search or date range.'
                : 'Sales you ring up in the POS will show here.'
            }
          />
        ) : (
          <>
            <Table>
              {header}
              <Table.Body>
                {query.data.items.map((s) => (
                  <Table.Row key={s.id}>
                    <Table.Cell>
                      <Link
                        to={`/sales/${s.id}`}
                        className="font-semibold text-primary-700 hover:underline"
                      >
                        {s.saleNumber}
                      </Link>
                    </Table.Cell>
                    <Table.Cell>{new Date(s.createdAtUtc).toLocaleString()}</Table.Cell>
                    <Table.Cell align="right">{s.itemCount}</Table.Cell>
                    <Table.Cell align="right" className="font-semibold text-text-primary">
                      {formatMoney(s.grandTotal)}
                    </Table.Cell>
                    <Table.Cell>{s.paymentSummary}</Table.Cell>
                    <Table.Cell>
                      <StatusBadge status={s.status} />
                    </Table.Cell>
                  </Table.Row>
                ))}
              </Table.Body>
            </Table>
            <Pagination
              page={query.data.page}
              pageSize={query.data.pageSize}
              totalCount={query.data.totalCount}
              totalPages={query.data.totalPages}
              onPageChange={q.setPage}
            />
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
