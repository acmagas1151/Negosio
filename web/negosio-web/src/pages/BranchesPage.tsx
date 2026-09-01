import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Building2 } from 'lucide-react'
import { ApiError } from '../api/client'
import { branchesApi } from '../api/branches'
import type { BranchDto } from '../api/types'
import { BranchFormModal } from '../components/branches/BranchFormModal'
import { BranchStatusBadge } from '../components/branches/BranchStatusBadge'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Button,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  SearchInput,
  Select,
  SkeletonText,
  Table,
  useToast,
} from '../components/ui'

const STATUS_OPTIONS = ['Active', 'Inactive'] as const

export default function BranchesPage() {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [search, setSearch] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [formOpen, setFormOpen] = useState(false)
  const [editing, setEditing] = useState<BranchDto | null>(null)
  const [confirm, setConfirm] = useState<{ branch: BranchDto; action: 'deactivate' | 'reactivate' } | null>(null)

  const query = useQuery({
    queryKey: ['branches', 'manage'],
    queryFn: () => branchesApi.list({ includeInactive: true }),
  })

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase()
    return (query.data ?? []).filter((b) => {
      if (statusFilter === 'Active' && !b.isActive) return false
      if (statusFilter === 'Inactive' && b.isActive) return false
      if (!term) return true
      return b.name.toLowerCase().includes(term) || b.code.toLowerCase().includes(term)
    })
  }, [query.data, search, statusFilter])

  const mutation = useMutation({
    mutationFn: ({ branch, action }: NonNullable<typeof confirm>) =>
      action === 'deactivate' ? branchesApi.deactivate(branch.id) : branchesApi.reactivate(branch.id),
    onSuccess: (_data, { action }) => {
      qc.invalidateQueries({ queryKey: ['branches'] })
      toast('success', action === 'deactivate' ? 'Branch deactivated' : 'Branch reactivated')
      setConfirm(null)
    },
    onError: (err) => toast('error', err instanceof ApiError ? err.message : 'Something went wrong.'),
  })

  const filtered = Boolean(search || statusFilter)

  const header = (
    <Table.Head>
      <Table.HeaderCell>Name</Table.HeaderCell>
      <Table.HeaderCell>Code</Table.HeaderCell>
      <Table.HeaderCell>Location</Table.HeaderCell>
      <Table.HeaderCell>Status</Table.HeaderCell>
      <Table.HeaderCell align="right">Actions</Table.HeaderCell>
    </Table.Head>
  )

  return (
    <DashboardLayout title="Branches">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Branches</h1>
          <Button size="sm" onClick={() => { setEditing(null); setFormOpen(true) }}>
            Add branch
          </Button>
        </div>

        <div className="flex flex-col gap-3 sm:flex-row">
          <SearchInput
            className="sm:max-w-xs"
            value={search}
            onChange={setSearch}
            placeholder="Search name or code"
          />
          <Select
            aria-label="Status"
            className="sm:max-w-[12rem]"
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value)}
          >
            <option value="">All statuses</option>
            {STATUS_OPTIONS.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </Select>
        </div>

        {query.isError ? (
          <ErrorState message={(query.error as Error).message} onRetry={() => query.refetch()} />
        ) : query.isPending ? (
          <Table>
            {header}
            <Table.Body>
              {Array.from({ length: 3 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: 5 }).map((__, j) => (
                    <Table.Cell key={j}>
                      <SkeletonText className={j === 0 ? 'w-32' : 'w-20'} />
                    </Table.Cell>
                  ))}
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        ) : rows.length === 0 ? (
          <EmptyState
            icon={Building2}
            title={filtered ? 'No branches match these filters' : 'One branch so far'}
            description={
              filtered
                ? 'Try clearing the search or filters.'
                : 'Add a branch to run inventory, registers and sales as a separate location.'
            }
            action={
              !filtered ? (
                <Button size="sm" onClick={() => { setEditing(null); setFormOpen(true) }}>
                  Add branch
                </Button>
              ) : undefined
            }
          />
        ) : (
          <Table>
            {header}
            <Table.Body>
              {rows.map((b) => (
                <Table.Row key={b.id}>
                  <Table.Cell className="font-medium text-text-primary">{b.name}</Table.Cell>
                  <Table.Cell>{b.code}</Table.Cell>
                  <Table.Cell className="text-text-secondary">
                    {[b.city, b.province].filter(Boolean).join(', ') || '—'}
                  </Table.Cell>
                  <Table.Cell>
                    <BranchStatusBadge active={b.isActive} />
                  </Table.Cell>
                  <Table.Cell align="right">
                    <div className="flex justify-end gap-1">
                      <Button variant="ghost" size="sm" onClick={() => { setEditing(b); setFormOpen(true) }}>
                        Edit
                      </Button>
                      {b.isActive ? (
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => setConfirm({ branch: b, action: 'deactivate' })}
                        >
                          Deactivate
                        </Button>
                      ) : (
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => setConfirm({ branch: b, action: 'reactivate' })}
                        >
                          Reactivate
                        </Button>
                      )}
                    </div>
                  </Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        )}
      </div>

      <BranchFormModal open={formOpen} onClose={() => setFormOpen(false)} branch={editing} />
      <ConfirmDialog
        open={confirm !== null}
        onClose={() => setConfirm(null)}
        onConfirm={() => confirm && mutation.mutate(confirm)}
        title={confirm?.action === 'deactivate' ? 'Deactivate branch' : 'Reactivate branch'}
        message={
          confirm?.action === 'deactivate'
            ? 'Staff assigned to this branch will lose access until you reactivate it or reassign them. New POS, register and stock activity is blocked. Historical data is kept.'
            : 'The branch becomes selectable for operational work again, and its assigned staff can sign in.'
        }
        confirmLabel={confirm?.action === 'deactivate' ? 'Deactivate' : 'Reactivate'}
        tone={confirm?.action === 'deactivate' ? 'danger' : 'default'}
        loading={mutation.isPending}
      />
    </DashboardLayout>
  )
}
