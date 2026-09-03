import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { salesApi } from '../../api/pos'
import type { SaleDetailDto } from '../../api/types'
import { VOID_INELIGIBLE_MESSAGES } from '../../lib/pos'
import { Button, Callout, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sale: SaleDetailDto
  onVoided: (updated: SaleDetailDto) => void
}

export function VoidSaleModal({ open, onClose, sale, onVoided }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [reason, setReason] = useState('')
  const [needsApproval, setNeedsApproval] = useState(false)
  const [approverEmail, setApproverEmail] = useState('')
  const [approverPassword, setApproverPassword] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setReason('')
    setNeedsApproval(false)
    setApproverEmail('')
    setApproverPassword('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      salesApi.void(sale.sale.id, {
        reason,
        approval: needsApproval ? { approverEmail, approverPassword } : undefined,
      }),
    onSuccess: (updated) => {
      qc.invalidateQueries({ queryKey: ['sales'] })
      qc.invalidateQueries({ queryKey: ['inventory'] })
      qc.invalidateQueries({ queryKey: ['session', 'current'] })
      toast('success', 'Sale voided')
      onVoided(updated)
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'VOID_APPROVAL_REQUIRED') {
        // First submit from a Cashier without the grant: reveal the approval fields, keep the reason.
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
      if (err instanceof ApiError && err.code in VOID_INELIGIBLE_MESSAGES) {
        setError(VOID_INELIGIBLE_MESSAGES[err.code])
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not void this sale.')
    },
  })

  const canSubmit = reason.trim().length > 0 && (!needsApproval || (approverEmail.trim() && approverPassword))

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={`Void sale #${sale.sale.saleNumber}`}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            variant="destructive"
            onClick={() => mutation.mutate()}
            loading={mutation.isPending}
            disabled={!canSubmit}
          >
            {needsApproval ? 'Approve & void' : 'Void sale'}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <p className="mb-4 text-[13px] text-text-muted">
        This will reverse the full sale and restore inventory.
      </p>

      {needsApproval && (
        <Callout tone="info">
          Manager approval required. You don&rsquo;t have permission to void completed sales — an
          authorized Manager, Admin, or Owner must approve this void.
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

      <TextField
        label="Reason"
        name="reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        autoFocus={!needsApproval}
      />
    </Modal>
  )
}
