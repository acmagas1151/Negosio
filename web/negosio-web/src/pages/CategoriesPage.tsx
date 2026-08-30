import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Tag } from 'lucide-react'
import { categoriesApi } from '../api/catalog'
import type { CategoryDto } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { useCan } from '../lib/useCan'
import { CategoryFormModal } from '../components/catalog/CategoryFormModal'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  Button,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  Pagination,
  SearchInput,
  Select,
  SkeletonText,
  Table,
  useToast,
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
  const canWrite = useCan('catalog:write')
  const qc = useQueryClient()
  const { toast } = useToast()

  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<CategoryDto | null>(null)
  const [confirmTarget, setConfirmTarget] = useState<CategoryDto | null>(null)

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

  const deactivateMutation = useMutation({
    mutationFn: (category: CategoryDto) => categoriesApi.deactivate(category.id),
    onSuccess: (_data, category) => {
      qc.invalidateQueries({ queryKey: ['categories'] })
      toast('success', `${category.name} deactivated`)
      setConfirmTarget(null)
    },
    onError: (error) => toast('error', error instanceof Error ? error.message : 'Something went wrong.'),
  })

  const reactivateMutation = useMutation({
    mutationFn: (category: CategoryDto) =>
      categoriesApi.update(category.id, {
        name: category.name,
        description: category.description,
        isActive: true,
      }),
    onSuccess: (_data, category) => {
      qc.invalidateQueries({ queryKey: ['categories'] })
      toast('success', `${category.name} reactivated`)
    },
    onError: (error) => toast('error', error instanceof Error ? error.message : 'Something went wrong.'),
  })

  const rowBusy = deactivateMutation.isPending || reactivateMutation.isPending

  const openCreate = () => {
    setEditing(null)
    setModalOpen(true)
  }

  const openEdit = (category: CategoryDto) => {
    setEditing(category)
    setModalOpen(true)
  }

  return (
    <DashboardLayout title="Categories">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Categories</h1>
          {canWrite && (
            <Button size="sm" onClick={openCreate} disabled={rowBusy}>
              New category
            </Button>
          )}
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
              {canWrite && <Table.HeaderCell align="right">Actions</Table.HeaderCell>}
            </Table.Head>
            <Table.Body>
              {Array.from({ length: 5 }).map((_, i) => (
                <Table.Row key={i}>
                  <Table.Cell><SkeletonText className="w-32" /></Table.Cell>
                  <Table.Cell><SkeletonText className="w-48" /></Table.Cell>
                  <Table.Cell align="right"><SkeletonText className="ml-auto w-8" /></Table.Cell>
                  <Table.Cell><SkeletonText className="w-16" /></Table.Cell>
                  {canWrite && (
                    <Table.Cell align="right"><SkeletonText className="ml-auto w-24" /></Table.Cell>
                  )}
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
                {canWrite && <Table.HeaderCell align="right">Actions</Table.HeaderCell>}
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
                    {canWrite && (
                      <Table.Cell align="right">
                        <div className="flex justify-end gap-1">
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => openEdit(c)}
                            disabled={rowBusy}
                          >
                            Edit
                          </Button>
                          {c.isActive ? (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => setConfirmTarget(c)}
                              disabled={rowBusy}
                            >
                              Deactivate
                            </Button>
                          ) : (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => reactivateMutation.mutate(c)}
                              disabled={rowBusy}
                              loading={
                                reactivateMutation.isPending &&
                                reactivateMutation.variables?.id === c.id
                              }
                            >
                              Reactivate
                            </Button>
                          )}
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

      {canWrite && (
        <CategoryFormModal
          open={modalOpen}
          onClose={() => setModalOpen(false)}
          category={editing}
        />
      )}

      {canWrite && (
        <ConfirmDialog
          open={confirmTarget !== null}
          onClose={() => setConfirmTarget(null)}
          onConfirm={() => confirmTarget && deactivateMutation.mutate(confirmTarget)}
          title="Deactivate category"
          message="Products keep this category, but it will be hidden from category pickers. You can reactivate it later."
          confirmLabel="Deactivate"
          tone="danger"
          loading={deactivateMutation.isPending}
        />
      )}
    </DashboardLayout>
  )
}
