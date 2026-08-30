import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { Package, Plus } from 'lucide-react'
import { categoriesApi, productsApi } from '../api/catalog'
import type { ProductSortBy } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { useCan } from '../lib/useCan'
import { formatMoney, formatRange } from '../lib/format'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  Button,
  EmptyState,
  ErrorState,
  Pagination,
  SearchInput,
  Select,
  SkeletonText,
  Table,
} from '../components/ui'

type ProductFilters = { categoryId?: string; status?: string; tracking?: string }

const DEFAULT_FILTERS = {
  categoryId: undefined,
  status: undefined,
  tracking: undefined,
} as const

export default function ProductsPage() {
  const navigate = useNavigate()
  const canWrite = useCan('catalog:write')
  const canViewCost = useCan('costs:view')

  const q = usePagedQuery<ProductFilters>({ defaultFilters: DEFAULT_FILTERS })

  const categoriesQuery = useQuery({
    queryKey: ['categories', 'all-active'],
    queryFn: categoriesApi.listAllActive,
  })

  const query = useQuery({
    queryKey: [
      'products',
      {
        page: q.page,
        search: q.search,
        categoryId: q.filters.categoryId,
        status: q.filters.status,
        tracking: q.filters.tracking,
        sortBy: q.sortBy,
        sortDirection: q.sortDirection,
      },
    ],
    queryFn: () =>
      productsApi.list({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        categoryId: q.filters.categoryId,
        isActive:
          q.filters.status === 'active' ? true : q.filters.status === 'inactive' ? false : undefined,
        trackInventory:
          q.filters.tracking === 'tracked'
            ? true
            : q.filters.tracking === 'untracked'
              ? false
              : undefined,
        sortBy: (q.sortBy as ProductSortBy | undefined) ?? undefined,
        sortDirection: q.sortDirection,
      }),
  })

  const newProductButton = canWrite ? (
    <Button
      size="sm"
      leadingIcon={<Plus className="size-4" aria-hidden="true" />}
      onClick={() => navigate('/products/new')}
    >
      New product
    </Button>
  ) : null

  const columnCount = canViewCost ? 7 : 6

  return (
    <DashboardLayout title="Products">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Products</h1>
          {newProductButton}
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search name, SKU or barcode"
          />
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
            aria-label="Status"
            className="sm:max-w-[10rem]"
            value={q.filters.status ?? ''}
            onChange={(e) => q.setFilter('status', e.target.value || undefined)}
          >
            <option value="">All statuses</option>
            <option value="active">Active</option>
            <option value="inactive">Inactive</option>
          </Select>
          <Select
            aria-label="Tracking"
            className="sm:max-w-[10rem]"
            value={q.filters.tracking ?? ''}
            onChange={(e) => q.setFilter('tracking', e.target.value || undefined)}
          >
            <option value="">All tracking</option>
            <option value="tracked">Tracked</option>
            <option value="untracked">Not tracked</option>
          </Select>
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            <Table.Head>
              <Table.HeaderCell>Product</Table.HeaderCell>
              <Table.HeaderCell>Category</Table.HeaderCell>
              <Table.HeaderCell align="right">Price</Table.HeaderCell>
              {canViewCost && <Table.HeaderCell align="right">Cost</Table.HeaderCell>}
              <Table.HeaderCell>Variants</Table.HeaderCell>
              <Table.HeaderCell>Tracking</Table.HeaderCell>
              <Table.HeaderCell>Status</Table.HeaderCell>
            </Table.Head>
            <Table.Body>
              {Array.from({ length: 8 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: columnCount }).map((__, j) => (
                    <Table.Cell key={j} align={j === 2 || (canViewCost && j === 3) ? 'right' : 'left'}>
                      <SkeletonText
                        className={
                          j === 2 || (canViewCost && j === 3) ? 'ml-auto w-16' : 'w-24'
                        }
                      />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={Package}
            title="No products"
            description="Create your first product to start selling."
            action={newProductButton}
          />
        ) : (
          <>
            <Table>
              <Table.Head>
                <Table.HeaderCell
                  sortKey="name"
                  activeSort={{ by: q.sortBy, dir: q.sortDirection }}
                  onSort={q.setSort}
                >
                  Product
                </Table.HeaderCell>
                <Table.HeaderCell>Category</Table.HeaderCell>
                <Table.HeaderCell
                  align="right"
                  sortKey="sellingprice"
                  activeSort={{ by: q.sortBy, dir: q.sortDirection }}
                  onSort={q.setSort}
                >
                  Price
                </Table.HeaderCell>
                {canViewCost && <Table.HeaderCell align="right">Cost</Table.HeaderCell>}
                <Table.HeaderCell>Variants</Table.HeaderCell>
                <Table.HeaderCell>Tracking</Table.HeaderCell>
                <Table.HeaderCell>Status</Table.HeaderCell>
              </Table.Head>
              <Table.Body>
                {query.data.items.map((p) => (
                  <Table.Row key={p.id}>
                    <Table.Cell>
                      <Link
                        to={`/products/${p.id}`}
                        className="font-semibold text-primary-700 hover:underline"
                      >
                        {p.name}
                      </Link>
                      {p.description && (
                        <span className="line-clamp-1 text-text-muted">{p.description}</span>
                      )}
                    </Table.Cell>
                    <Table.Cell>{p.categoryName}</Table.Cell>
                    <Table.Cell align="right">
                      {p.hasVariants
                        ? formatRange(p.minSellingPrice, p.maxSellingPrice, formatMoney)
                        : formatMoney(p.minSellingPrice)}
                    </Table.Cell>
                    {canViewCost && (
                      <Table.Cell align="right">
                        {p.minCostPrice == null
                          ? '—'
                          : p.hasVariants && p.maxCostPrice != null
                            ? formatRange(p.minCostPrice, p.maxCostPrice, formatMoney)
                            : formatMoney(p.minCostPrice)}
                      </Table.Cell>
                    )}
                    <Table.Cell>{p.hasVariants ? p.variantCount : 'Simple'}</Table.Cell>
                    <Table.Cell>
                      {p.trackInventory ? (
                        <Badge tone="blue">Tracked</Badge>
                      ) : (
                        <span className="text-text-muted">—</span>
                      )}
                    </Table.Cell>
                    <Table.Cell>
                      <Badge tone={p.isActive ? 'success' : 'neutral'}>
                        {p.isActive ? 'Active' : 'Inactive'}
                      </Badge>
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
