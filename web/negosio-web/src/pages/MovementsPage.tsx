import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ArrowLeft, History } from 'lucide-react'
import { productsApi } from '../api/catalog'
import { branchesApi, inventoryApi } from '../api/inventory'
import type { StockMovementType } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { formatQty, formatSignedQty } from '../lib/format'
import { movementTypeLabel } from '../lib/movements'
import { MovementTypeBadge } from '../components/inventory/MovementTypeBadge'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  EmptyState,
  ErrorState,
  Pagination,
  Select,
  SkeletonText,
  Table,
} from '../components/ui'

type Filters = {
  branchId: string | undefined
  productId: string | undefined
  type: string | undefined
  from: string | undefined
  to: string | undefined
}

const DEFAULT_FILTERS: Filters = {
  branchId: undefined,
  productId: undefined,
  type: undefined,
  from: undefined,
  to: undefined,
}

const TYPE_OPTIONS: StockMovementType[] = [
  'OpeningStock',
  'AdjustmentIncrease',
  'AdjustmentDecrease',
  'Sale',
  'Return',
  'TransferIn',
  'TransferOut',
  'Purchase',
  'Waste',
]

export default function MovementsPage() {
  const q = usePagedQuery<Filters>({ defaultFilters: DEFAULT_FILTERS })

  const branchesQuery = useQuery({ queryKey: ['branches'], queryFn: () => branchesApi.list() })
  const productsQuery = useQuery({
    queryKey: ['products', 'inventory-picker'],
    queryFn: () =>
      productsApi.list({ isActive: true, trackInventory: true, pageSize: 100, sortBy: 'name' }),
  })
  const multiBranch = (branchesQuery.data?.length ?? 0) > 1

  const query = useQuery({
    queryKey: [
      'inventory',
      'movements',
      { page: q.page, ...q.filters },
    ],
    queryFn: () =>
      inventoryApi.movements({
        page: q.page,
        pageSize: q.pageSize,
        branchId: q.filters.branchId,
        productId: q.filters.productId,
        type: (q.filters.type as StockMovementType | undefined) ?? undefined,
        fromUtc: q.filters.from ? `${q.filters.from}T00:00:00Z` : undefined,
        toUtc: q.filters.to ? `${q.filters.to}T23:59:59Z` : undefined,
      }),
  })

  const colCount = multiBranch ? 8 : 7

  const header = (
    <Table.Head>
      <Table.HeaderCell>When</Table.HeaderCell>
      <Table.HeaderCell>Product</Table.HeaderCell>
      <Table.HeaderCell>Type</Table.HeaderCell>
      <Table.HeaderCell align="right">Change</Table.HeaderCell>
      <Table.HeaderCell align="right">Balance</Table.HeaderCell>
      <Table.HeaderCell>Reason</Table.HeaderCell>
      <Table.HeaderCell>By</Table.HeaderCell>
      {multiBranch && <Table.HeaderCell>Branch</Table.HeaderCell>}
    </Table.Head>
  )

  return (
    <DashboardLayout title="Stock movements">
      <div className="space-y-5">
        <div className="flex flex-col gap-1">
          <Link
            to="/inventory"
            className="inline-flex items-center gap-1 text-sm font-medium text-text-secondary hover:text-text-primary"
          >
            <ArrowLeft className="size-4" aria-hidden="true" />
            Inventory
          </Link>
          <h1 className="text-2xl font-bold text-text-primary">Stock movements</h1>
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-end">
          {multiBranch && (
            <Select
              aria-label="Branch"
              className="sm:max-w-[12rem]"
              value={q.filters.branchId ?? ''}
              onChange={(e) => q.setFilter('branchId', e.target.value || undefined)}
            >
              <option value="">All branches</option>
              {branchesQuery.data?.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </Select>
          )}
          <Select
            aria-label="Product"
            className="sm:max-w-[14rem]"
            value={q.filters.productId ?? ''}
            onChange={(e) => q.setFilter('productId', e.target.value || undefined)}
          >
            <option value="">All products</option>
            {productsQuery.data?.items.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </Select>
          <Select
            aria-label="Movement type"
            className="sm:max-w-[12rem]"
            value={q.filters.type ?? ''}
            onChange={(e) => q.setFilter('type', e.target.value || undefined)}
          >
            <option value="">All types</option>
            {TYPE_OPTIONS.map((t) => (
              <option key={t} value={t}>
                {movementTypeLabel(t)}
              </option>
            ))}
          </Select>
          <label className="flex flex-col gap-1 text-sm font-semibold text-text-secondary">
            From
            <input
              type="date"
              value={q.filters.from ?? ''}
              onChange={(e) => q.setFilter('from', e.target.value || undefined)}
              className="h-11 rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
            />
          </label>
          <label className="flex flex-col gap-1 text-sm font-semibold text-text-secondary">
            To
            <input
              type="date"
              value={q.filters.to ?? ''}
              onChange={(e) => q.setFilter('to', e.target.value || undefined)}
              className="h-11 rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
            />
          </label>
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            {header}
            <Table.Body>
              {Array.from({ length: 8 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: colCount }).map((__, j) => (
                    <Table.Cell key={j}>
                      <SkeletonText className={j === 1 ? 'w-40' : 'w-16'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={History}
            title="No stock movements"
            description="No movements match these filters. Adjustments, sales and returns appear here as they happen."
          />
        ) : (
          <>
            <Table>
              {header}
              <Table.Body>
                {query.data.items.map((m) => (
                  <Table.Row key={m.id}>
                    <Table.Cell className="whitespace-nowrap text-text-secondary">
                      {new Date(m.createdAtUtc).toLocaleString()}
                    </Table.Cell>
                    <Table.Cell>
                      <Link
                        to={`/products/${m.productId}`}
                        className="font-medium text-primary-700 hover:underline"
                      >
                        {m.productName}
                      </Link>
                      {m.variantName && m.variantName !== 'Default' && (
                        <span className="block text-[13px] text-text-muted">{m.variantName}</span>
                      )}
                    </Table.Cell>
                    <Table.Cell>
                      <MovementTypeBadge type={m.type} />
                    </Table.Cell>
                    <Table.Cell
                      align="right"
                      className={
                        m.quantity > 0
                          ? 'font-semibold text-success-strong'
                          : m.quantity < 0
                            ? 'font-semibold text-danger-strong'
                            : 'text-text-muted'
                      }
                    >
                      {formatSignedQty(m.quantity)}
                    </Table.Cell>
                    <Table.Cell align="right" className="whitespace-nowrap text-text-secondary">
                      {formatQty(m.quantityBefore)} → {formatQty(m.quantityAfter)}
                    </Table.Cell>
                    <Table.Cell>
                      <span className="line-clamp-1 text-text-muted">{m.reason ?? '—'}</span>
                    </Table.Cell>
                    <Table.Cell className="text-text-secondary">{m.createdByName}</Table.Cell>
                    {multiBranch && <Table.Cell>{m.branchName}</Table.Cell>}
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
