import { useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Users } from 'lucide-react'
import { ApiError } from '../api/client'
import { staffApi } from '../api/staff'
import type { StaffMemberDto } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { roleLabel } from '../lib/roles'
import { isBranchScoped } from '../lib/roles'
import { ChangeBranchModal } from '../components/staff/ChangeBranchModal'
import { ChangeRoleModal } from '../components/staff/ChangeRoleModal'
import { InviteStaffModal } from '../components/staff/InviteStaffModal'
import { RoleBadge, StaffStatusBadge } from '../components/staff/StaffBadges'
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

const ROLE_OPTIONS = ['Owner', 'Admin', 'Manager', 'Cashier', 'InventoryStaff', 'KitchenStaff', 'Viewer'] as const
const STATUS_OPTIONS = ['Active', 'Deactivated', 'Invited', 'Expired'] as const

function displayName(m: StaffMemberDto): string {
  const name = `${m.firstName ?? ''} ${m.lastName ?? ''}`.trim()
  return name || '—'
}

export default function StaffPage() {
  const qc = useQueryClient()
  const { toast } = useToast()
  const { user } = useAuth()

  const [search, setSearch] = useState('')
  const [roleFilter, setRoleFilter] = useState('')
  const [statusFilter, setStatusFilter] = useState('')
  const [inviteOpen, setInviteOpen] = useState(false)
  const [roleTarget, setRoleTarget] = useState<StaffMemberDto | null>(null)
  const [branchTarget, setBranchTarget] = useState<StaffMemberDto | null>(null)
  const [confirm, setConfirm] = useState<{ member: StaffMemberDto; action: 'deactivate' | 'reactivate' | 'revoke' } | null>(null)

  const query = useQuery({ queryKey: ['staff'], queryFn: staffApi.list })

  const rows = useMemo(() => {
    const term = search.trim().toLowerCase()
    return (query.data ?? []).filter((m) => {
      if (roleFilter && m.role !== roleFilter) return false
      if (statusFilter && m.status !== statusFilter) return false
      if (!term) return true
      return (
        m.email.toLowerCase().includes(term) ||
        `${m.firstName ?? ''} ${m.lastName ?? ''}`.toLowerCase().includes(term)
      )
    })
  }, [query.data, search, roleFilter, statusFilter])

  const mutation = useMutation({
    mutationFn: async ({ member, action }: NonNullable<typeof confirm>) => {
      if (action === 'deactivate') await staffApi.deactivate(member.id)
      else if (action === 'reactivate') await staffApi.reactivate(member.id)
      else await staffApi.revokeInvitation(member.id)
    },
    onSuccess: (_data, { action }) => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast(
        'success',
        action === 'deactivate'
          ? 'Staff member deactivated'
          : action === 'reactivate'
            ? 'Staff member reactivated'
            : 'Invitation revoked',
      )
      setConfirm(null)
    },
    onError: (err) => toast('error', err instanceof ApiError ? err.message : 'Something went wrong.'),
  })

  const resend = useMutation({
    mutationFn: (id: string) => staffApi.resendInvitation(id),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', 'Invitation resent')
    },
    onError: (err) => toast('error', err instanceof ApiError ? err.message : 'Could not resend.'),
  })

  const isSelf = (m: StaffMemberDto) => m.kind === 'Member' && m.id === user?.id
  const isOwnerRow = (m: StaffMemberDto) => m.role === 'Owner'
  // An Admin cannot act on an Owner; nobody can act on themselves.
  const canManage = (m: StaffMemberDto) =>
    !isSelf(m) && !(isOwnerRow(m) && user?.role !== 'Owner')

  const filtered = Boolean(search || roleFilter || statusFilter)

  const header = (
    <Table.Head>
      <Table.HeaderCell>Name</Table.HeaderCell>
      <Table.HeaderCell>Email</Table.HeaderCell>
      <Table.HeaderCell>Role</Table.HeaderCell>
      <Table.HeaderCell>Branch</Table.HeaderCell>
      <Table.HeaderCell>Status</Table.HeaderCell>
      <Table.HeaderCell align="right">Actions</Table.HeaderCell>
    </Table.Head>
  )

  return (
    <DashboardLayout title="Staff">
      <div className="space-y-5">
        <div className="flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
          <h1 className="text-2xl font-bold text-text-primary">Staff</h1>
          <Button size="sm" onClick={() => setInviteOpen(true)}>
            Invite staff
          </Button>
        </div>

        <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
          <SearchInput
            className="sm:max-w-xs"
            value={search}
            onChange={setSearch}
            placeholder="Search name or email"
          />
          <Select
            aria-label="Role"
            className="sm:max-w-[12rem]"
            value={roleFilter}
            onChange={(e) => setRoleFilter(e.target.value)}
          >
            <option value="">All roles</option>
            {ROLE_OPTIONS.map((r) => (
              <option key={r} value={r}>
                {roleLabel(r)}
              </option>
            ))}
          </Select>
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
              {Array.from({ length: 4 }).map((_, i) => (
                <Table.Row key={i}>
                  {Array.from({ length: 6 }).map((__, j) => (
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
            icon={Users}
            title={filtered ? 'No staff match these filters' : 'It’s just you so far'}
            description={
              filtered
                ? 'Try clearing the search or filters.'
                : 'Invite your team so they can use Negosio with their own accounts.'
            }
            action={
              !filtered ? (
                <Button size="sm" onClick={() => setInviteOpen(true)}>
                  Invite staff
                </Button>
              ) : undefined
            }
          />
        ) : (
          <Table>
            {header}
            <Table.Body>
              {rows.map((m) => (
                <Table.Row key={m.id}>
                  <Table.Cell className="font-medium text-text-primary">
                    {displayName(m)}
                    {isSelf(m) && <span className="ml-1.5 text-[12px] text-text-muted">(you)</span>}
                  </Table.Cell>
                  <Table.Cell>{m.email}</Table.Cell>
                  <Table.Cell>
                    <RoleBadge role={m.role} />
                  </Table.Cell>
                  <Table.Cell className="text-text-secondary">{m.branchName ?? '—'}</Table.Cell>
                  <Table.Cell>
                    <StaffStatusBadge status={m.status} />
                  </Table.Cell>
                  <Table.Cell align="right">
                    <div className="flex justify-end gap-1">
                      {m.kind === 'Invitation' ? (
                        <>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => resend.mutate(m.id)}
                            loading={resend.isPending && resend.variables === m.id}
                          >
                            Resend
                          </Button>
                          <Button
                            variant="ghost"
                            size="sm"
                            onClick={() => setConfirm({ member: m, action: 'revoke' })}
                          >
                            Revoke
                          </Button>
                        </>
                      ) : canManage(m) ? (
                        <>
                          <Button variant="ghost" size="sm" onClick={() => setRoleTarget(m)}>
                            Change role
                          </Button>
                          {isBranchScoped(m.role) && (
                            <Button variant="ghost" size="sm" onClick={() => setBranchTarget(m)}>
                              Change branch
                            </Button>
                          )}
                          {m.status === 'Deactivated' ? (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => setConfirm({ member: m, action: 'reactivate' })}
                            >
                              Reactivate
                            </Button>
                          ) : (
                            <Button
                              variant="ghost"
                              size="sm"
                              onClick={() => setConfirm({ member: m, action: 'deactivate' })}
                            >
                              Deactivate
                            </Button>
                          )}
                        </>
                      ) : (
                        <span className="text-[13px] text-text-muted">—</span>
                      )}
                    </div>
                  </Table.Cell>
                </Table.Row>
              ))}
            </Table.Body>
          </Table>
        )}
      </div>

      <InviteStaffModal open={inviteOpen} onClose={() => setInviteOpen(false)} />
      <ChangeRoleModal open={roleTarget !== null} onClose={() => setRoleTarget(null)} member={roleTarget} />
      <ChangeBranchModal open={branchTarget !== null} onClose={() => setBranchTarget(null)} member={branchTarget} />
      <ConfirmDialog
        open={confirm !== null}
        onClose={() => setConfirm(null)}
        onConfirm={() => confirm && mutation.mutate(confirm)}
        title={
          confirm?.action === 'deactivate'
            ? 'Deactivate staff member'
            : confirm?.action === 'reactivate'
              ? 'Reactivate staff member'
              : 'Revoke invitation'
        }
        message={
          confirm?.action === 'deactivate'
            ? 'They will be signed out and cannot sign in again until reactivated. Their past sales and sessions stay on record.'
            : confirm?.action === 'reactivate'
              ? 'They will be able to sign in again with their existing password.'
              : 'The invitation link will stop working. You can invite this email again later.'
        }
        confirmLabel={
          confirm?.action === 'deactivate'
            ? 'Deactivate'
            : confirm?.action === 'reactivate'
              ? 'Reactivate'
              : 'Revoke'
        }
        tone={confirm?.action === 'reactivate' ? 'default' : 'danger'}
        loading={mutation.isPending}
      />
    </DashboardLayout>
  )
}
