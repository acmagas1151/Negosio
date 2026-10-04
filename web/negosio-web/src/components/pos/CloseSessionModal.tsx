import { useEffect, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
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
  /** A deliberate second step between entering the counted cash and actually committing the close —
   * closing a session ends the cashier's POS session and can't be undone, so a single click should
   * never be enough to trigger it. */
  const [confirming, setConfirming] = useState(false)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setClosingCash('')
    setError('')
    setResult(null)
    setConfirming(false)
  }, [open])

  const mutation = useMutation({
    mutationFn: () =>
      (isForce ? sessionsApi.forceClose : sessionsApi.close)(session!.id, {
        closingCash: Number(closingCash),
      }),
    onSuccess: (closed) => setResult(closed),
    onError: (err) => {
      setConfirming(false)
      setError(err instanceof Error ? err.message : 'Could not close the session.')
    },
  })

  const reviewClose = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setError('')
    const value = Number(closingCash)
    if (!(value >= 0) || closingCash.trim() === '') {
      setError('Enter the counted cash amount (0 or more).')
      return
    }
    setConfirming(true)
  }

  const confirmClose = () => {
    if (mutation.isPending) return
    mutation.mutate()
  }

  // Advisory only — fetched fresh each time the confirm step opens, so it reflects any sale/void/cash
  // movement up to that moment. The actual close recomputes this itself under its own lock; a race
  // between this preview and the real close is never load-bearing, just possibly a touch stale.
  const preview = useQuery({
    queryKey: ['register-session', session?.id, 'expected-cash'],
    queryFn: () => sessionsApi.previewExpectedCash(session!.id),
    enabled: confirming && !!session,
  })

  if (!session) return null

  // Matches RegisterSession.Close's own convention exactly: CashDifference = closingCash - expectedCash.
  // Positive (counted more than expected) = over; negative (counted less than expected) = short.
  const previewDifference = preview.data ? Number(closingCash) - preview.data.expectedCash : null
  const previewOver = (previewDifference ?? 0) >= 0

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
        ) : confirming ? (
          <>
            <Button variant="secondary" size="sm" onClick={() => setConfirming(false)} disabled={mutation.isPending}>
              Back
            </Button>
            <Button
              size="sm"
              variant="destructive"
              onClick={confirmClose}
              loading={mutation.isPending}
            >
              {isForce ? 'Confirm force close' : 'Confirm close'}
            </Button>
          </>
        ) : (
          <>
            <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
              Cancel
            </Button>
            <Button
              size="sm"
              variant={isForce ? 'destructive' : 'primary'}
              onClick={reviewClose}
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

          {result.grossCashSales != null && (
            <>
              <dt className="text-text-muted">Gross cash sales</dt>
              <dd className="text-right text-text-secondary">{formatMoney(result.grossCashSales)}</dd>
            </>
          )}
          {result.voidedCashSales != null && result.voidedCashSales > 0 && (
            <>
              <dt className="text-text-muted">Voided cash sales</dt>
              <dd className="text-right text-danger-strong">−{formatMoney(result.voidedCashSales)}</dd>
            </>
          )}
          {result.grossCashSales != null && result.voidedCashSales != null && (
            <>
              <dt className="font-medium text-text-secondary">Net cash sales</dt>
              <dd className="text-right font-medium text-text-secondary">
                {formatMoney(result.grossCashSales - result.voidedCashSales)}
              </dd>
            </>
          )}
          {result.refundCashOut != null && result.refundCashOut > 0 && (
            <>
              <dt className="text-text-muted">Refund cash out</dt>
              <dd className="text-right text-danger-strong">−{formatMoney(result.refundCashOut)}</dd>
            </>
          )}
          {result.cashIn != null && result.cashIn > 0 && (
            <>
              <dt className="text-text-muted">Cash in</dt>
              <dd className="text-right text-text-secondary">{formatMoney(result.cashIn)}</dd>
            </>
          )}
          {result.cashOut != null && result.cashOut > 0 && (
            <>
              <dt className="text-text-muted">Cash out</dt>
              <dd className="text-right text-danger-strong">−{formatMoney(result.cashOut)}</dd>
            </>
          )}

          <dt className="pt-1 font-semibold text-text-primary">Expected cash</dt>
          <dd className="pt-1 text-right font-semibold text-text-primary">{formatMoney(result.expectedCash ?? 0)}</dd>
          <dt className="text-text-muted">Counted</dt>
          <dd className="text-right text-text-secondary">{formatMoney(result.closingCash ?? 0)}</dd>
          <dt className="pt-1 font-semibold text-text-primary">Difference</dt>
          <dd className={cn('pt-1 text-right font-semibold', over ? 'text-success-strong' : 'text-danger-strong')}>
            {formatMoney(difference)} {over ? 'over' : 'short'}
          </dd>
        </dl>
      ) : confirming ? (
        <div>
          <Callout tone="warning">
            {isForce
              ? `This will force close ${session.registerName} with ₱${closingCash} counted, and cannot be undone.`
              : `This will close ${session.registerName} and end your POS session. This cannot be undone.`}
          </Callout>
          <dl className="mt-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 rounded-lg bg-surface-subtle px-3 py-3 text-sm">
            <dt className="text-text-muted">Register</dt>
            <dd className="text-right text-text-secondary">{session.registerName}</dd>
            <dt className="text-text-muted">Opening cash</dt>
            <dd className="text-right text-text-secondary">{formatMoney(session.openingCash)}</dd>
            <dt className="text-text-muted">Counted cash in drawer</dt>
            <dd className="text-right font-semibold text-text-primary">{formatMoney(Number(closingCash))}</dd>

            {preview.isLoading && (
              <>
                <dt className="pt-1 text-text-muted">Expected cash</dt>
                <dd className="pt-1 text-right text-text-muted">Calculating…</dd>
              </>
            )}
            {preview.data && (
              <>
                <dt className="pt-1 font-semibold text-text-primary">Expected cash</dt>
                <dd className="pt-1 text-right font-semibold text-text-primary">
                  {formatMoney(preview.data.expectedCash)}
                </dd>
                <dt className="font-semibold text-text-primary">Difference</dt>
                <dd
                  className={cn(
                    'text-right font-semibold',
                    previewOver ? 'text-success-strong' : 'text-danger-strong',
                  )}
                >
                  {formatMoney(previewDifference ?? 0)} {previewOver ? 'over' : 'short'}
                </dd>
              </>
            )}
            {preview.isError && (
              <>
                <dt className="pt-1 text-text-muted">Expected cash</dt>
                <dd className="pt-1 text-right text-text-muted">Unavailable — will be shown after closing.</dd>
              </>
            )}
          </dl>
        </div>
      ) : (
        <form onSubmit={reviewClose}>
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
