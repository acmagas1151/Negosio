import { useQuery } from '@tanstack/react-query'
import { Tag } from 'lucide-react'
import { categoriesApi } from '../api/catalog'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  EmptyState,
  ErrorState,
  Pagination,
  SearchInput,
  Select,
  SkeletonText,
  Table,
} from '../components/ui'

type StatusFilter = { status: string | undefined }

const DEFAULT_FILTERS: StatusFilter = { status: undefined }

function isActiveParam(status: string | undefined): boolean | undefined {
  if (status === 'active') return true
  if (status === 'inactive') return false
  return undefined
}

export default function CategoriesPage() {
  const q = usePagedQuery<StatusFilter>({ defaultFilters: DEFAULT_FILTERS })

  const query = useQuery({
    queryKey: ['categories', { page: q.page, search: q.search, status: q.filters.status }],
    queryFn: () =>
      categoriesApi.list({
        page: q.page,
        pageSize: q.pageSize,
        search: q.search || undefined,
        isActive: isActiveParam(q.filters.status),
      }),
  })

  return (
    <DashboardLayout title="Categories">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Categories</h1>
        </div>

        <div className="flex flex-col gap-3 sm:flex-row">
          <SearchInput
            className="sm:max-w-xs"
            value={q.searchInput}
            onChange={q.setSearchInput}
            placeholder="Search categories"
          />
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
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            <Table.Head>
              <Table.HeaderCell>Name</Table.HeaderCell>
              <Table.HeaderCell>Description</Table.HeaderCell>
              <Table.HeaderCell align="right">Products</Table.HeaderCell>
              <Table.HeaderCell>Status</Table.HeaderCell>
            </Table.Head>
            <Table.Body>
              {Array.from({ length: 5 }).map((_, i) => (
                <Table.Row key={i}>
                  <Table.Cell><SkeletonText className="w-32" /></Table.Cell>
                  <Table.Cell><SkeletonText className="w-48" /></Table.Cell>
                  <Table.Cell align="right"><SkeletonText className="ml-auto w-8" /></Table.Cell>
                  <Table.Cell><SkeletonText className="w-16" /></Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={Tag}
            title="No categories"
            description="Categories group your products and power the product filters."
          />
        ) : (
          <>
            <Table>
              <Table.Head>
                <Table.HeaderCell>Name</Table.HeaderCell>
                <Table.HeaderCell>Description</Table.HeaderCell>
                <Table.HeaderCell align="right">Products</Table.HeaderCell>
                <Table.HeaderCell>Status</Table.HeaderCell>
              </Table.Head>
              <Table.Body>
                {query.data.items.map((c) => (
                  <Table.Row key={c.id}>
                    <Table.Cell className="font-semibold text-text-primary">{c.name}</Table.Cell>
                    <Table.Cell>
                      <span className="line-clamp-1 text-text-muted">{c.description ?? '—'}</span>
                    </Table.Cell>
                    <Table.Cell align="right">{c.productCount}</Table.Cell>
                    <Table.Cell>
                      <Badge tone={c.isActive ? 'success' : 'neutral'}>
                        {c.isActive ? 'Active' : 'Inactive'}
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
