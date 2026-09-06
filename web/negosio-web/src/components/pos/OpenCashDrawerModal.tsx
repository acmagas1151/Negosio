import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { sessionsApi } from '../../api/pos'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sessionId: string
  onOpened: () => void
}

/**
 * Authorizes + audits a no-sale cash-drawer open. Mirrors CancelTransactionModal/VoidSaleModal's
 * submit-then-reveal-approval flow (same backend error-code shape) — no hardware/device
 * integration exists yet, so this never claims a physical drawer opened, only that the request was
 * authorized and recorded.
 */
export function OpenCashDrawerModal({ open, onClose, sessionId, onOpened }: Props) {
  const qc = useQueryClient()
  const [needsApproval, setNeedsApproval] = useState(false)
  const [approverEmail, setApproverEmail] = useState('')
  const [approverPassword, setApproverPassword] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setNeedsApproval(false)
    setApproverEmail('')
    setApproverPassword('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      sessionsApi.cashDrawer.open(sessionId, {
        approval: needsApproval ? { approverEmail, approverPassword } : undefined,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['session', 'current'] })
      onOpened()
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'CASH_DRAWER_APPROVAL_REQUIRED') {
        // First submit from a Cashier without the grant: reveal the approval fields.
        setNeedsApproval(true)
        setError('')
        return
      }
      if (err instanceof ApiError && err.code === 'INVALID_APPROVER_CREDENTIALS') {
        setError('Invalid manager credentials.')
        return
      }
      if (err instanceof ApiError && err.code === 'VOID_APPROVER_WRONG_BRANCH') {
        setError('This manager cannot approve this for your branch.')
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not open the cash drawer.')
    },
  })

  const canSubmit = !needsApproval || (approverEmail.trim() && approverPassword)

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Open cash drawer"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!canSubmit}
          >
            {needsApproval ? 'Approve & open' : 'Open drawer'}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <p className="text-[13px] text-text-secondary">
        This opens the drawer without a sale and is recorded against your session for
        reconciliation.
      </p>

      {needsApproval && (
        <Callout tone="info">
          Manager approval required. You don&rsquo;t have permission to open the cash drawer
          directly — an authorized Manager, Admin, or Owner must approve this.
        </Callout>
      )}

      {needsApproval && (
        <>
          <TextField
            label="Manager account"
            name="approverEmail"
            type="email"
            value={approverEmail}
            onChange={(e) => setApproverEmail(e.target.value)}
            autoFocus
          />
          <TextField
            label="Password"
            name="approverPassword"
            type="password"
            value={approverPassword}
            onChange={(e) => setApproverPassword(e.target.value)}
          />
        </>
      )}
    </Modal>
  )
}
