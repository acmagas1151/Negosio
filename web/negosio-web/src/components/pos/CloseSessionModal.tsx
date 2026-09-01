import { useEffect, useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { sessionsApi } from '../../api/pos'
import type { RegisterSessionDto } from '../../api/types'
import { formatMoney } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField } from '../ui'

/** Just enough of a session to reconcile + close it. RegisterSessionDto satisfies this. */
interface SessionLike {
  id: string
  registerName: string
  openingCash: number
}

interface Props {
  open: boolean
  onClose: () => void
  session: SessionLike | null
  onClosed: (session: RegisterSessionDto) => void
  /** 'force' = Owner/Admin closing another user's session (same reconciliation). */
  variant?: 'self' | 'force'
  openedByName?: string
}

export function CloseSessionModal({
  open,
  onClose,
  session,
  onClosed,
  variant = 'self',
  openedByName,
}: Props) {
  const isForce = variant === 'force'
  const [closingCash, setClosingCash] = useState('')
  const [error, setError] = useState('')
  const [result, setResult] = useState<RegisterSessionDto | null>(null)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setClosingCash('')
    setError('')
    setResult(null)
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      (isForce ? sessionsApi.forceClose : sessionsApi.close)(session!.id, {
        closingCash: Number(closingCash),
      }),
    onSuccess: (closed) => setResult(closed),
    onError: (err) => setError(err instanceof Error ? err.message : 'Could not close the session.'),
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setError('')
    const value = Number(closingCash)
    if (!(value >= 0) || closingCash.trim() === '') {
      setError('Enter the counted cash amount (0 or more).')
      return
    }
    mutation.mutate()
  }

  if (!session) return null

  const difference = result?.cashDifference ?? 0
  const over = difference >= 0

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={isForce ? `Force close ${session.registerName}` : 'Close register session'}
      footer={
        result ? (
          <Button
            size="sm"
            onClick={() => {
              onClosed(result)
              onClose()
            }}
          >
            Done
          </Button>
        ) : (
          <>
            <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
              Cancel
            </Button>
            <Button
              size="sm"
              variant={isForce ? 'destructive' : 'primary'}
              onClick={submit}
              loading={mutation.isPending}
            >
              {isForce ? 'Force close session' : 'Close session'}
            </Button>
          </>
        )
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      {result ? (
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 rounded-lg bg-surface-subtle px-3 py-3 text-sm">
          <dt className="text-text-muted">Opening cash</dt>
          <dd className="text-right text-text-secondary">{formatMoney(result.openingCash)}</dd>
          <dt className="text-text-muted">Expected in drawer</dt>
          <dd className="text-right text-text-secondary">{formatMoney(result.expectedCash ?? 0)}</dd>
          <dt className="text-text-muted">Counted</dt>
          <dd className="text-right text-text-secondary">{formatMoney(result.closingCash ?? 0)}</dd>
          <dt className="pt-1 font-semibold text-text-primary">Difference</dt>
          <dd
            className={cn(
              'pt-1 text-right font-semibold',
              over ? 'text-success-strong' : 'text-danger-strong',
            )}
          >
            {formatMoney(difference)} {over ? 'over' : 'short'}
          </dd>
        </dl>
      ) : (
        <form onSubmit={submit}>
          <p className="mb-4 text-[13px] text-text-muted">
            Register: <span className="font-medium text-text-secondary">{session.registerName}</span>{' '}
            · opened with {formatMoney(session.openingCash)}
            {isForce && openedByName ? ` by ${openedByName}` : ''}
          </p>
          {isForce && (
            <div className="mb-3">
              <Callout tone="warning">
                This session belongs to {openedByName ?? 'another user'}. Counting the drawer and
                force closing releases the register; the session stays on record under its original
                operator.
              </Callout>
            </div>
          )}
          <TextField
            label="Counted cash in drawer"
            name="closingCash"
            type="number"
            min={0}
            step="0.01"
            value={closingCash}
            onChange={(e) => setClosingCash(e.target.value)}
            error={error || undefined}
            autoFocus
          />
        </form>
      )}
    </Modal>
  )
}
