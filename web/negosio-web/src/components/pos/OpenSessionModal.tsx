import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { ApiError } from '../../api/client'
import { sessionsApi } from '../../api/pos'
import type { RegisterSessionDto } from '../../api/types'
import { Button, Callout, Modal, TextField } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  registerId: string
  registerName: string
  onOpened: (session: RegisterSessionDto) => void
}

export function OpenSessionModal({ open, onClose, registerId, registerName, onOpened }: Props) {
  const [openingCash, setOpeningCash] = useState('')
  const [error, setError] = useState('')
  const [alreadyOpen, setAlreadyOpen] = useState(false)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setOpeningCash('')
    setError('')
    setAlreadyOpen(false)
  }, [open])

  const mutation = useMutation({
    mutationFn: () => sessionsApi.open({ registerId, openingCash: Number(openingCash) }),
    onSuccess: (session) => {
      onOpened(session)
      onClose()
    },
    onError: (err) => {
      if (
        err instanceof ApiError &&
        err.status === 409 &&
        err.code === 'REGISTER_SESSION_ALREADY_OPEN'
      ) {
        setAlreadyOpen(true)
        return
      }
      setError(err instanceof Error ? err.message : 'Could not open the session.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setError('')
    const value = Number(openingCash)
    if (!(value >= 0) || openingCash.trim() === '') {
      setError('Enter the opening cash amount (0 or more).')
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Open register session"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            Open session
          </Button>
        </>
      }
    >
      {alreadyOpen && (
        <Callout tone="warning">
          This register already has an open session. Close it before opening a new one.
        </Callout>
      )}

      <p className="mb-4 text-[13px] text-text-muted">
        Register: <span className="font-medium text-text-secondary">{registerName}</span>
      </p>

      <form onSubmit={submit}>
        <TextField
          label="Opening cash"
          name="openingCash"
          type="number"
          min={0}
          step="0.01"
          value={openingCash}
          onChange={(e) => setOpeningCash(e.target.value)}
          error={error || undefined}
          hint="How much cash is in the drawer at the start of the shift."
          autoFocus
        />
      </form>
    </Modal>
  )
}
