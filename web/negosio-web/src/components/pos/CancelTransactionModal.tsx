import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { posApi } from '../../api/pos'
import { formatMoney } from '../../lib/format'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  branchId: string
  itemCount: number
  grandTotal: number
  /** Called once authorization succeeds — the caller clears the cart. Nothing here is persisted:
   * no Sale is created and no SaleNumber is consumed, so there is nothing to roll back on close. */
  onAuthorized: () => void
}

/**
 * Authorizes cancelling the cart currently at the register. Mirrors VoidSaleModal's
 * submit-then-reveal-approval flow (same backend error codes), but there is no Sale behind this —
 * a cart that hasn't checked out has no SaleNumber, so a successful authorization only ever clears
 * local state, never touches the backend beyond the authorization check itself.
 */
export function CancelTransactionModal({
  open,
  onClose,
  branchId,
  itemCount,
  grandTotal,
  onAuthorized,
}: Props) {
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
      posApi.authorizeCancelTransaction({
        branchId,
        approval: needsApproval ? { approverEmail, approverPassword } : undefined,
      }),
    onSuccess: () => {
      onAuthorized()
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'VOID_APPROVAL_REQUIRED') {
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
        setError('This manager cannot approve voids for this branch.')
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not authorize this action.')
    },
  })

  const canSubmit = !needsApproval || (approverEmail.trim() && approverPassword)

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Cancel current transaction"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Keep transaction
          </Button>
          <Button
            size="sm"
            variant="destructive"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!canSubmit}
          >
            {needsApproval ? 'Approve & cancel' : 'Cancel transaction'}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <div className="mb-4 rounded-xl border border-border bg-surface-subtle p-3 text-[13px]">
        <p className="font-semibold text-text-primary">You are about to cancel:</p>
        <p className="mt-1">
          {itemCount} item{itemCount === 1 ? '' : 's'} in the cart ·{' '}
          <span className="font-semibold text-text-primary">{formatMoney(grandTotal)}</span>
        </p>
        <p className="mt-2 text-text-muted">This will:</p>
        <ul className="ml-4 list-disc text-text-muted">
          <li>clear the cart, discounts, and payment state</li>
          <li>nothing is recorded — no sale is created and no sale number is used</li>
        </ul>
      </div>

      {needsApproval && (
        <Callout tone="info">
          Manager approval required. You don&rsquo;t have permission to cancel a transaction
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
