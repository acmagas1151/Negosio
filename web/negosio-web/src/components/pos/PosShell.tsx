import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { LogOut } from 'lucide-react'
import { useAuth } from '../../auth/AuthContext'
import type { RegisterSessionDto } from '../../api/types'
import { formatMoney, formatTime } from '../../lib/format'
import { useCan } from '../../lib/useCan'
import { Button } from '../ui'

interface Props {
  session: RegisterSessionDto
  register: { id: string; name: string; code?: string }
  branchName?: string | null
  branchCode?: string | null
  onCloseSession: () => void
  onCashIn: () => void
  onCashOut: () => void
  children: ReactNode
}

/** Full-screen POS chrome — no dashboard sidebar. Session header + body. */
export function PosShell({
  session,
  register,
  branchName,
  branchCode,
  onCloseSession,
  onCashIn,
  onCashOut,
  children,
}: Props) {
  const { user } = useAuth()
  const canRecordCashMovement = useCan('register:cash-movement')

  return (
    <div className="flex h-screen flex-col bg-background">
      <header className="flex shrink-0 flex-col gap-1.5 border-b border-border bg-surface px-4 py-2.5">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div className="flex items-baseline gap-2 truncate">
            <span className="truncate font-bold text-text-primary">{user?.tenantName}</span>
            <span className="text-text-muted">·</span>
            <span className="font-semibold text-text-secondary">POS</span>
          </div>
          <div className="flex items-center gap-4">
            {/* Session-operation actions — kept visually separate from the transaction toolbar below. */}
            <div className="flex items-center gap-2">
              {canRecordCashMovement && (
                <>
                  <Button variant="secondary" size="sm" onClick={onCashIn}>
                    Cash in
                  </Button>
                  <Button variant="secondary" size="sm" onClick={onCashOut}>
                    Cash out
                  </Button>
                </>
              )}
              <Button variant="secondary" size="sm" onClick={onCloseSession}>
                Close session
              </Button>
              <Link
                to="/dashboard"
                className="inline-flex h-9 items-center gap-1.5 rounded-lg border border-border-strong bg-white px-3 text-sm font-semibold text-text-secondary hover:bg-surface-subtle hover:text-text-primary"
              >
                <LogOut className="size-4" aria-hidden="true" />
                Exit
              </Link>
            </div>
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-[13px]">
          {branchCode && (
            <span className="font-semibold text-text-primary">{branchCode}</span>
          )}
          {branchName && <span className="text-text-muted">{branchName}</span>}
          {(branchCode || branchName) && <span className="text-text-muted">·</span>}
          <span className="font-semibold text-text-primary">{register.name}</span>
          <span className="text-text-muted">·</span>
          <span className="text-text-secondary">
            {session.status} · {formatMoney(session.openingCash)}
            {session.openedByName ? ` · ${session.openedByName}` : ''}
          </span>
          <span className="text-text-muted">
            · Session started {formatTime(session.openedAtUtc)}
          </span>
        </div>
      </header>
      <div className="min-h-0 flex-1">{children}</div>
    </div>
  )
}
