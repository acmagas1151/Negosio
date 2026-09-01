import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { branchesApi } from '../../api/branches'
import { staffApi } from '../../api/staff'
import type { StaffMemberDto } from '../../api/types'
import { Button, Callout, Modal, Select, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  member: StaffMemberDto | null
}

export function ChangeBranchModal({ open, onClose, member }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [branchId, setBranchId] = useState('')
  const [error, setError] = useState('')

  const branchesQuery = useQuery({
    queryKey: ['branches', 'active'],
    queryFn: () => branchesApi.list(),
    enabled: open,
  })
  const branches = branchesQuery.data ?? []

  useEffect(() => {
    if (!open || !member) return
    // oxlint-disable-next-line set-state-in-effect
    setBranchId(member.branchId ?? '')
    setError('')
  }, [open, member])

  const mutation = useMutation({
    mutationFn: () => staffApi.changeBranch(member!.id, { branchId }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', 'Branch updated')
      onClose()
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not change the branch.'),
  })

  if (!member) return null

  const name = `${member.firstName ?? ''} ${member.lastName ?? ''}`.trim() || member.email

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Change branch"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!branchId || branchId === member.branchId}
          >
            Save
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}
      <p className="mb-3 text-sm text-text-secondary">
        <span className="font-semibold text-text-primary">{name}</span> · currently{' '}
        {member.branchName ?? 'unassigned'}
      </p>
      <Select
        label="Branch"
        name="branchId"
        value={branchId}
        onChange={(e) => setBranchId(e.target.value)}
      >
        <option value="">Select a branch</option>
        {branches.map((b) => (
          <option key={b.id} value={b.id}>
            {b.name}
          </option>
        ))}
      </Select>
      <Callout tone="info">
        Takes effect on their next request — no sign-out needed. Blocked while they have an open
        register session.
      </Callout>
    </Modal>
  )
}
