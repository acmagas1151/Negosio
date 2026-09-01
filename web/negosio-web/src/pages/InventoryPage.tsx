import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { History, PackageSearch, Warehouse } from 'lucide-react'
import { categoriesApi } from '../api/catalog'
import { branchesApi, inventoryApi } from '../api/inventory'
import type { InventoryRowDto } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { useCan } from '../lib/useCan'
import { formatQty } from '../lib/format'
import { AdjustStockModal } from '../components/inventory/AdjustStockModal'
import { OpeningStockModal } from '../components/inventory/OpeningStockModal'
import { StockStatusBadge } from '../components/inventory/StockStatusBadge'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Button,
  EmptyState,
  ErrorState,
  Pagination,
  SearchInput,
  Select,
  SkeletonText,
  Table,
} from '../components/ui'

type Filters = { branchId: string | undefined; categoryId: string | undefined; level: string | undefined }

const DEFAULT_FILTERS: Filters = { branchId: undefined, categoryId: undefined, level: undefined }

export default function InventoryPage() {
  const q = usePagedQuery<Filters>({ defaultFilters: DEFAULT_FILTERS })
  const canWrite = useCan('inventory:write')

  const [adjustRow, setAdjustRow] = useState<InventoryRowDto | null>(null)
  const [openingOpen, setOpeningOpen] = useState(false)

  const branchesQuery = useQuery({ queryKey: ['branches'], queryFn: () => branchesApi.list() })
  const categoriesQuery = useQuery({
    queryKey: ['categories', 'all-active'],
    queryFn: categoriesApi.listAllActive,
  })

  const multiBranch = (branchesQuery.data?.length ?? 0) > 1

  const query = useQuery({
    queryKey: [
      'inventory',
      {
        page: q.page,
        search: q.search,
        branchId: q.filters.branchId,
        categoryId: q.filters.categoryId,
        level: q.filters.level,
      },
    ],
    queryFn: () =>
      inventoryApi.list({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        branchId: q.filters.branchId,
        categoryId: q.filters.categoryId,
        lowStock: q.filters.level === 'low' ? true : undefined,
      }),
  })

  const colCount = multiBranch ? (canWrite ? 7 : 6) : canWrite ? 6 : 5
  const filtered = Boolean(q.search || q.filters.branchId || q.filters.categoryId || q.filters.level)

  const header = (
    <Table.Head>
      <Table.HeaderCell>Product</Table.HeaderCell>
      <Table.HeaderCell>SKU</Table.HeaderCell>
      {multiBranch && <Table.HeaderCell>Branch</Table.HeaderCell>}
      <Table.HeaderCell align="right">On hand</Table.HeaderCell>
      <Table.HeaderCell align="right">Reorder level</Table.HeaderCell>
      <Table.HeaderCell>Status</Table.HeaderCell>
      {canWrite && <Table.HeaderCell align="right">Actions</Table.HeaderCell>}
    </Table.Head>
  )

  return (
    <DashboardLayout title="Inventory">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Stock levels</h1>
          <div className="flex items-center gap-2">
            <Link
              to="/inventory/movements"
              className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-border-strong bg-white px-3 text-sm font-semibold text-text-secondary hover:bg-surface-subtle hover:text-text-primary"
            >
              <History className="size-4" aria-hidden="true" />
              Movement history
            </Link>
            {canWrite && (
              <Button size="sm" onClick={() => setOpeningOpen(true)}>
                Add opening stock
              </Button>
            )}
          </div>
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search product, SKU or barcode"
          />
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
            aria-label="Category"
            className="sm:max-w-[12rem]"
            value={q.filters.categoryId ?? ''}
            onChange={(e) => q.setFilter('categoryId', e.target.value || undefined)}
          >
            <option value="">All categories</option>
            {categoriesQuery.data?.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </Select>
          <Select
            aria-label="Stock level"
            className="sm:max-w-[12rem]"
            value={q.filters.level ?? ''}
            onChange={(e) => q.setFilter('level', e.target.value || undefined)}
          >
            <option value="">All stock levels</option>
            <option value="low">Low &amp; out of stock</option>
          </Select>
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
                      <SkeletonText className={j === 0 ? 'w-40' : 'w-16'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={filtered ? PackageSearch : Warehouse}
            title={filtered ? 'No stock matches these filters' : 'Nothing is stocked yet'}
            description={
              filtered
                ? 'Try clearing the search or filters.'
                : 'Add opening stock for a product to start tracking quantities.'
            }
            action={
              canWrite && !filtered ? (
                <Button size="sm" onClick={() => setOpeningOpen(true)}>
                  Add opening stock
                </Button>
              ) : undefined
            }
          />
        ) : (
          <>
            <Table>
              {header}
              <Table.Body>
                {query.data.items.map((r) => (
                  <Table.Row key={r.id}>
                    <Table.Cell>
                      <Link
                        to={`/products/${r.productId}`}
                        className="font-semibold text-primary-700 hover:underline"
                      >
                        {r.productName}
                      </Link>
                      {!r.isDefaultVariant && (
                        <span className="block text-[13px] text-text-muted">{r.variantName}</span>
                      )}
                    </Table.Cell>
                    <Table.Cell>{r.sku ?? '—'}</Table.Cell>
                    {multiBranch && <Table.Cell>{r.branchName}</Table.Cell>}
                    <Table.Cell align="right" className="font-semibold text-text-primary">
                      {formatQty(r.quantityOnHand)}
                    </Table.Cell>
                    <Table.Cell align="right" className="text-text-muted">
                      {formatQty(r.reorderLevel)}
                    </Table.Cell>
                    <Table.Cell>
                      <StockStatusBadge status={r.status} />
                    </Table.Cell>
                    {canWrite && (
                      <Table.Cell align="right">
                        <div className="flex justify-end gap-1">
                          <Button variant="ghost" size="sm" onClick={() => setAdjustRow(r)}>
                            Adjust
                          </Button>
                          <Link
                            to={`/inventory/movements?productId=${r.productId}`}
                            className="inline-flex h-9 items-center rounded-lg px-3 text-sm font-semibold text-text-secondary hover:bg-surface-subtle"
                          >
                            History
                          </Link>
                        </div>
                      </Table.Cell>
                    )}
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

      <AdjustStockModal open={adjustRow !== null} onClose={() => setAdjustRow(null)} row={adjustRow} />
      {canWrite && (
        <OpeningStockModal
          open={openingOpen}
          onClose={() => setOpeningOpen(false)}
          branches={branchesQuery.data ?? []}
        />
      )}
    </DashboardLayout>
  )
}
