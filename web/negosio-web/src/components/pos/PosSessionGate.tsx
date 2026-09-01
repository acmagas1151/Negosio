import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { ApiError } from '../../api/client'
import { sessionsApi } from '../../api/pos'
import { Button, TextField } from '../ui'

interface Props {
  register: { id: string; name: string }
  onOpened: () => void
  onSwitchRegister: () => void
}

export function PosSessionGate({ register, onOpened, onSwitchRegister }: Props) {
  const [openingCash, setOpeningCash] = useState('')
  const [error, setError] = useState('')

  const mutation = useMutation({
    mutationFn: () => sessionsApi.open({ registerId: register.id, openingCash: Number(openingCash) }),
    onSuccess: () => onOpened(),
    onError: (err) => {
      if (err instanceof ApiError && err.status === 409 && err.code === 'REGISTER_SESSION_ALREADY_OPEN') {
        // Someone took this register first — send the operator back to re-pick.
        onSwitchRegister()
        return
      }
      if (err instanceof ApiError && err.status === 409 && err.code === 'CASHIER_SESSION_OPEN') {
        setError('You already have an open session on another register. Continue or close it first.')
        return
      }
      setError(err instanceof Error ? err.message : 'Could not open the session.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setError('')
    if (openingCash.trim() === '' || !(Number(openingCash) >= 0)) {
      setError('Enter the opening cash amount (0 or more).')
      return
    }
    mutation.mutate()
  }

  return (
    <div className="w-full max-w-sm space-y-4 rounded-2xl border border-border bg-surface p-6 shadow-card-lg">
      <div className="space-y-1">
        <h1 className="text-lg font-bold text-text-primary">Open {register.name}</h1>
        <p className="text-[13px] text-text-muted">
          This register has no open session. Enter the starting cash to begin.
        </p>
      </div>

      <form onSubmit={submit} className="space-y-4">
        <TextField
          label="Opening cash"
          name="openingCash"
          type="number"
          min={0}
          step="0.01"
          value={openingCash}
          onChange={(e) => setOpeningCash(e.target.value)}
          error={error || undefined}
          autoFocus
        />
        <Button block onClick={submit} loading={mutation.isPending}>
          Open register
        </Button>
      </form>

      <div className="flex items-center justify-between border-t border-border pt-3 text-[13px]">
        <button
          type="button"
          onClick={onSwitchRegister}
          className="font-semibold text-primary-700 hover:underline"
        >
          ← Back to registers
        </button>
        <Link to="/dashboard" className="font-semibold text-text-muted hover:underline">
          Exit POS
        </Link>
      </div>
    </div>
  )
}
