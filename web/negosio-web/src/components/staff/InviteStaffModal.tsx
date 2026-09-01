import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Check, Copy } from 'lucide-react'
import { ApiError } from '../../api/client'
import { branchesApi } from '../../api/branches'
import { staffApi } from '../../api/staff'
import type { StaffInvitationResultDto, UserRole } from '../../api/types'
import { useAuth } from '../../auth/AuthContext'
import { fieldErrorsFrom } from '../../lib/formErrors'
import { assignableRoles, isBranchScoped, roleLabel } from '../../lib/roles'
import { Button, Callout, Modal, Select, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
}

export function InviteStaffModal({ open, onClose }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const { user } = useAuth()
  const roles = assignableRoles(user?.role ?? 'Viewer')

  const [email, setEmail] = useState('')
  const [role, setRole] = useState<UserRole>(roles[0] ?? 'Cashier')
  const [branchId, setBranchId] = useState('')
  const [emailError, setEmailError] = useState('')
  const [branchError, setBranchError] = useState('')
  const [formError, setFormError] = useState('')
  const [result, setResult] = useState<StaffInvitationResultDto | null>(null)
  const [copied, setCopied] = useState(false)

  const branchesQuery = useQuery({
    queryKey: ['branches', 'active'],
    queryFn: () => branchesApi.list(),
    enabled: open,
  })
  const branches = branchesQuery.data ?? []
  const needsBranch = isBranchScoped(role)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setEmail('')
    setRole(roles[0] ?? 'Cashier')
    setBranchId('')
    setEmailError('')
    setBranchError('')
    setFormError('')
    setResult(null)
    setCopied(false)
  }, [open]) // eslint-disable-line react-hooks/exhaustive-deps

  const mutation = useMutation({
    mutationFn: () =>
      staffApi.invite({ email: email.trim(), role, branchId: needsBranch ? branchId : null }),
    onSuccess: (data) => {
      qc.invalidateQueries({ queryKey: ['staff'] })
      toast('success', `Invitation sent to ${data.email}`)
      // No email provider yet: if the backend handed back a link, keep the modal open to show it.
      if (data.acceptPath) setResult(data)
      else onClose()
    },
    onError: (err) => {
      const fields = fieldErrorsFrom(err)
      if (fields.email) {
        setEmailError(fields.email)
        return
      }
      if (err instanceof ApiError) {
        setFormError(err.message)
        return
      }
      toast('error', 'Could not send the invitation.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setEmailError('')
    setBranchError('')
    setFormError('')
    if (!email.trim()) {
      setEmailError('Enter an email address.')
      return
    }
    if (needsBranch && !branchId) {
      setBranchError('Choose a branch for this role.')
      return
    }
    mutation.mutate()
  }

  const acceptUrl = result ? `${window.location.origin}${result.acceptPath}` : ''

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={result ? 'Invitation created' : 'Invite staff'}
      footer={
        result ? (
          <Button size="sm" onClick={onClose}>
            Done
          </Button>
        ) : (
          <>
            <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
              Cancel
            </Button>
            <Button size="sm" onClick={submit} loading={mutation.isPending}>
              Send invitation
            </Button>
          </>
        )
      }
    >
      {result ? (
        <div className="space-y-3">
          <Callout tone="info">
            No email is sent yet. Share this link with <strong>{result.email}</strong> so they can
            set a password and join as {roleLabel(result.role)}.
          </Callout>
          <div className="flex items-center gap-2 rounded-lg border border-border-strong bg-surface-subtle px-3 py-2">
            <code className="flex-1 truncate text-[13px] text-text-secondary">{acceptUrl}</code>
            <Button
              variant="ghost"
              size="sm"
              onClick={() => {
                navigator.clipboard?.writeText(acceptUrl).then(
                  () => {
                    setCopied(true)
                    setTimeout(() => setCopied(false), 1500)
                  },
                  () => undefined,
                )
              }}
              leadingIcon={
                copied ? (
                  <Check className="size-4" aria-hidden="true" />
                ) : (
                  <Copy className="size-4" aria-hidden="true" />
                )
              }
            >
              {copied ? 'Copied' : 'Copy'}
            </Button>
          </div>
        </div>
      ) : (
        <form onSubmit={submit} className="space-y-3">
          {formError && <Callout tone="error">{formError}</Callout>}
          <TextField
            label="Email"
            name="email"
            type="email"
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            error={emailError || undefined}
            autoFocus
          />
          <Select
            label="Role"
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
          {needsBranch && (
            <Select
              label="Branch"
              name="branchId"
              value={branchId}
              onChange={(e) => setBranchId(e.target.value)}
              error={branchError || undefined}
            >
              <option value="">Select a branch</option>
              {branches.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </Select>
          )}
          <p className="text-[13px] text-text-muted">
            They&rsquo;ll choose their own name and password when they accept.
          </p>
        </form>
      )}
    </Modal>
  )
}
