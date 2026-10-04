import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { sessionsApi } from '../../api/pos'
import type { CashMovementType } from '../../api/types'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  sessionId: string
  type: CashMovementType
  onDone: () => void
}

const COPY: Record<CashMovementType, { title: string; cta: string; placeholder: string }> = {
  CashIn: { title: 'Cash in', cta: 'Add cash', placeholder: 'Additional float' },
  CashOut: { title: 'Cash out', cta: 'Remove cash', placeholder: 'Petty cash' },
}

export function CashMovementModal({ open, onClose, sessionId, type, onDone }: Props) {
  const qc = useQueryClient()
  const [amount, setAmount] = useState('')
  const [reason, setReason] = useState('')
  const [error, setError] = useState('')
  const [needsApproval, setNeedsApproval] = useState(false)
  const [approverEmail, setApproverEmail] = useState('')
  const [approverPassword, setApproverPassword] = useState('')
  const copy = COPY[type]

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setAmount('')
    setReason('')
    setError('')
    setNeedsApproval(false)
    setApproverEmail('')
    setApproverPassword('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      sessionsApi.cashMovements.create(sessionId, {
        type,
        amount: Number(amount),
        reason,
        approval: needsApproval ? { approverEmail, approverPassword } : undefined,
      }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['session', 'current'] })
      onDone()
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'CASH_MOVEMENT_APPROVAL_REQUIRED') {
        // First submit from a Cashier without the grant: reveal the approval fields, keep the rest.
        setNeedsApproval(true)
        setError('')
        return
      }
      if (err instanceof ApiError && err.code === 'INVALID_APPROVER_CREDENTIALS') {
        setError('Invalid manager credentials.')
        return
      }
      if (err instanceof ApiError && err.code === 'VOID_APPROVER_WRONG_BRANCH') {
        setError('This manager cannot approve cash movements for this branch.')
        return
      }
      setError(err instanceof ApiError ? err.message : 'Could not record this movement.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    const value = Number(amount)
    if (!(value > 0)) {
      setError('Enter an amount greater than zero.')
      return
    }
    if (!reason.trim()) {
      setError('A reason is required.')
      return
    }
    if (needsApproval && (!approverEmail.trim() || !approverPassword)) {
      setError('Manager account and password are required.')
      return
    }
    setError('')
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={copy.title}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button
            size="sm"
            onClick={submit}
            loading={mutation.isPending}
            disabled={needsApproval && (!approverEmail.trim() || !approverPassword)}
          >
            {needsApproval ? 'Approve & continue' : copy.cta}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      {needsApproval && (
        <Callout tone="info">
          Manager approval required. You don&rsquo;t have permission to record cash movements — an
          authorized Manager, Admin, or Owner must approve this.
        </Callout>
      )}

      <form onSubmit={submit}>
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
          label="Amount"
          name="amount"
          type="number"
          min={0.01}
          step="0.01"
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
          autoFocus={!needsApproval}
        />
        <TextField
          label="Reason"
          name="reason"
          value={reason}
          onChange={(e) => setReason(e.target.value)}
          placeholder={copy.placeholder}
        />
      </form>
    </Modal>
  )
}
