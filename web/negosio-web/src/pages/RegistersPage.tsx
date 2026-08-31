import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Calculator } from 'lucide-react'
import { branchesApi } from '../api/inventory'
import { registersApi } from '../api/pos'
import type { RegisterDto, RegisterSessionDto } from '../api/types'
import { usePagedQuery } from '../hooks/usePagedQuery'
import { useCan } from '../lib/useCan'
import { RegisterFormModal } from '../components/registers/RegisterFormModal'
import { RegisterSessionCell } from '../components/registers/RegisterSessionCell'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  Button,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  Pagination,
  Select,
  SkeletonText,
  Table,
  useToast,
} from '../components/ui'

type Filters = { status: string | undefined; branchId: string | undefined }

const DEFAULT_FILTERS: Filters = { status: undefined, branchId: undefined }

function isActiveParam(status: string | undefined): boolean | undefined {
  if (status === 'active') return true
  if (status === 'inactive') return false
  return undefined
}

export default function RegistersPage() {
  const q = usePagedQuery<Filters>({ defaultFilters: DEFAULT_FILTERS })
  const canManage = useCan('register:manage')
  const qc = useQueryClient()
  const { toast } = useToast()

  const [modalOpen, setModalOpen] = useState(false)
  const [editing, setEditing] = useState<RegisterDto | null>(null)
  const [confirmTarget, setConfirmTarget] = useState<RegisterDto | null>(null)

  const branchesQuery = useQuery({ queryKey: ['branches'], queryFn: branchesApi.list })
  const branches = branchesQuery.data ?? []
  const multiBranch = branches.length > 1

  const query = useQuery({
    queryKey: ['registers', { page: q.page, status: q.filters.status, branchId: q.filters.branchId }],
    queryFn: () =>
      registersApi.list({
        page: q.page,
        pageSize: q.pageSize,
        isActive: isActiveParam(q.filters.status),
        branchId: q.filters.branchId,
      }),
  })

  const deactivateMutation = useMutation({
    mutationFn: (register: RegisterDto) => registersApi.deactivate(register.id),
    onSuccess: (_data, register) => {
      qc.invalidateQueries({ queryKey: ['registers'] })
      toast('success', `${register.name} deactivated`)
      setConfirmTarget(null)
    },
    onError: (error) =>
      toast('error', error instanceof Error ? error.message : 'Something went wrong.'),
  })

  const reactivateMutation = useMutation({
    mutationFn: (register: RegisterDto) =>
      registersApi.update(register.id, {
        name: register.name,
        code: register.code,
        isActive: true,
      }),
    onSuccess: (_data, register) => {
      qc.invalidateQueries({ queryKey: ['registers'] })
      toast('success', `${register.name} reactivated`)
    },
    onError: (error) =>
      toast('error', error instanceof Error ? error.message : 'Something went wrong.'),
  })

  const rowBusy = deactivateMutation.isPending || reactivateMutation.isPending

  const openCreate = () => {
    setEditing(null)
    setModalOpen(true)
  }
  const openEdit = (register: RegisterDto) => {
    setEditing(register)
    setModalOpen(true)
  }

  const targetHasOpenSession =
    confirmTarget != null &&
    qc.getQueryData<RegisterSessionDto>(['session', 'current', confirmTarget.id]) != null

  const colCount = multiBranch ? (canManage ? 6 : 5) : canManage ? 5 : 4

  const header = (
    <Table.Head>
      <Table.HeaderCell>Name</Table.HeaderCell>
      <Table.HeaderCell>Code</Table.HeaderCell>
      {multiBranch && <Table.HeaderCell>Branch</Table.HeaderCell>}
      <Table.HeaderCell>Status</Table.HeaderCell>
      <Table.HeaderCell>Session</Table.HeaderCell>
      {canManage && <Table.HeaderCell align="right">Actions</Table.HeaderCell>}
    </Table.Head>
  )

  return (
    <DashboardLayout title="Registers">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Registers</h1>
          {canManage && (
            <Button size="sm" onClick={openCreate} disabled={rowBusy}>
              New register
            </Button>
          )}
        </div>

        <div className="flex flex-col gap-3 sm:flex-row">
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
                </option>
              ))}
            </Select>
          )}
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            {header}
            <Table.Body>
              {Array.from({ length: 4 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: colCount }).map((__, j) => (
                    <Table.Cell key={j}>
                      <SkeletonText className={j === 0 ? 'w-32' : 'w-20'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : query.data.items.length === 0 ? (
          <EmptyState
            icon={Calculator}
            title="No registers yet"
            description="Create a register to start taking sales."
            action={
              canManage ? (
                <Button size="sm" onClick={openCreate}>
                  New register
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
                    <Table.Cell className="font-semibold text-text-primary">{r.name}</Table.Cell>
                    <Table.Cell>{r.code}</Table.Cell>
                    {multiBranch && <Table.Cell>{r.branchName}</Table.Cell>}
                    <Table.Cell>
                      <Badge tone={r.isActive ? 'success' : 'neutral'}>
                        {r.isActive ? 'Active' : 'Inactive'}
                      </Badge>
                    </Table.Cell>
                    <Table.Cell>
                      <RegisterSessionCell register={r} />
                    </Table.Cell>
                    {canManage && (
                      <Table.Cell align="right">
                        <div className="flex justify-end gap-1">
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => openEdit(r)}
                            disabled={rowBusy}
                          >
                            Edit
                          </Button>
                          {r.isActive ? (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => setConfirmTarget(r)}
                              disabled={rowBusy}
                            >
                              Deactivate
                            </Button>
                          ) : (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => reactivateMutation.mutate(r)}
                              disabled={rowBusy}
                              loading={
                                reactivateMutation.isPending &&
                                reactivateMutation.variables?.id === r.id
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

      {canManage && (
        <RegisterFormModal
          open={modalOpen}
          onClose={() => setModalOpen(false)}
          register={editing}
          branches={branches}
        />
      )}

      {canManage && (
        <ConfirmDialog
          open={confirmTarget !== null}
          onClose={() => setConfirmTarget(null)}
          onConfirm={() => confirmTarget && deactivateMutation.mutate(confirmTarget)}
          title="Deactivate register"
          message={
            targetHasOpenSession
              ? 'This register has an open session — closing it is recommended first. The register will stop accepting new sales.'
              : 'The register will stop accepting new sales. You can reactivate it later.'
          }
          confirmLabel="Deactivate"
          tone="danger"
          loading={deactivateMutation.isPending}
        />
      )}
    </DashboardLayout>
  )
}
