import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { staffApi } from '../../api/staff'
import type { StaffMemberDto, UserRole } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { assignableRoles, roleLabel } from '../../lib/roles'
import { Button, Callout, Modal, Select, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  member: StaffMemberDto | null
}

export function ChangeRoleModal({ open, onClose, member }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const { user } = useAuth()
  const roles = assignableRoles(user?.role ?? 'Viewer')

  const [role, setRole] = useState<UserRole>('Cashier')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open || !member) return
    // oxlint-disable-next-line set-state-in-effect
    setRole(roles.includes(member.role) ? member.role : (roles[0] ?? 'Cashier'))
    setError('')
  }, [open, member]) // eslint-disable-line react-hooks/exhaustive-deps

  const mutation = useMutation({
    mutationFn: () => staffApi.changeRole(member!.id, { role }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', 'Role updated')
      onClose()
    },
    onError: (err) => {
      setError(err instanceof ApiError ? err.message : 'Could not change the role.')
    },
  })

  if (!member) return null

  const name = `${member.firstName ?? ''} ${member.lastName ?? ''}`.trim() || member.email
  const unchanged = role === member.role

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Change role"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={() => mutation.mutate()} loading={mutation.isPending} disabled={unchanged}>
            Save
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}
      <p className="mb-3 text-sm text-text-secondary">
        <span className="font-semibold text-text-primary">{name}</span> · currently{' '}
        {roleLabel(member.role)}
      </p>
      <Select
        label="New role"
        name="role"
        value={role}
        onChange={(e) => setRole(e.target.value as UserRole)}
      >
        {roles.map((r) => (
          <option key={r} value={r}>
            {roleLabel(r)}
          </option>
        ))}
      </Select>
      <Callout tone="info">
        They&rsquo;ll need to sign out and back in for the new role to take effect.
      </Callout>
    </Modal>
  )
}
