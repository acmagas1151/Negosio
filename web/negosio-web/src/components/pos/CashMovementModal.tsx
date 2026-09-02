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
  const copy = COPY[type]

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setAmount('')
    setReason('')
    setError('')
  }, [open])

  const mutation = useMutation({
    mutationFn: () => sessionsApi.cashMovements.create(sessionId, { type, amount: Number(amount), reason }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['session', 'current'] })
      onDone()
      onClose()
    },
    onError: (err) => setError(err instanceof ApiError ? err.message : 'Could not record this movement.'),
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
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {copy.cta}
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}
      <form onSubmit={submit}>
        <TextField
          label="Amount"
          name="amount"
          type="number"
          min={0.01}
          step="0.01"
          value={amount}
          onChange={(e) => setAmount(e.target.value)}
          autoFocus
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
