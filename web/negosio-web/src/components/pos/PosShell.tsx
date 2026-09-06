import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { BanknoteArrowDown, BanknoteArrowUp, LogOut, Vault } from 'lucide-react'
import { useAuth } from '../../auth/AuthContext'
import type { RegisterSessionDto } from '../../api/types'
import { formatMoney, formatTime } from '../../lib/format'
import { useCan } from '../../lib/useCan'
import { Badge, Button } from '../ui'
import { PosTopNav } from './PosTopNav'

interface Props {
  session: RegisterSessionDto
  register: { id: string; name: string; code?: string }
  branchName?: string | null
  branchCode?: string | null
  onCloseSession: () => void
  onCashIn: () => void
  onCashOut: () => void
  onOpenCashDrawer: () => void
  children: ReactNode
}

/** Full-screen POS chrome — no dashboard sidebar. Top nav + session header + body. */
export function PosShell({
  session,
  register,
  branchName,
  branchCode,
  onCloseSession,
  onCashIn,
  onCashOut,
  onOpenCashDrawer,
  children,
}: Props) {
  const { user } = useAuth()
  const canRecordCashMovement = useCan('register:cash-movement')

  return (
    <div className="flex h-screen flex-col bg-background">
      <PosTopNav />

      <header className="flex shrink-0 flex-wrap items-center justify-between gap-2 border-b border-border bg-surface px-4 py-2.5">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="font-bold text-text-primary">{user?.tenantName}</span>
          <span className="text-text-muted">·</span>
          <span className="font-semibold text-text-secondary">POS</span>
          <Badge tone="success">{session.status}</Badge>

          <span className="mx-1 hidden text-border-strong sm:inline">|</span>
          <span className="w-full basis-full text-[13px] text-text-secondary sm:w-auto sm:basis-auto">
            {branchCode && <span className="font-semibold text-text-primary">{branchCode}</span>}
            {branchName && <span className="text-text-muted"> {branchName}</span>}
            <span className="text-text-muted"> · </span>
            <span className="font-semibold text-text-primary">{register.name}</span>
            <span className="text-text-muted"> · Opening cash {formatMoney(session.openingCash)}</span>
            {session.openedByName && <span className="text-text-muted"> · {session.openedByName}</span>}
            <span className="text-text-muted"> · Started {formatTime(session.openedAtUtc)}</span>
          </span>
        </div>

        <div className="flex items-center gap-2">
          {canRecordCashMovement && (
            <>
              <Button
                variant="secondary"
                size="sm"
                leadingIcon={<BanknoteArrowDown className="size-4" aria-hidden="true" />}
                onClick={onCashIn}
              >
                Cash in
              </Button>
              <Button
                variant="secondary"
                size="sm"
                leadingIcon={<BanknoteArrowUp className="size-4" aria-hidden="true" />}
                onClick={onCashOut}
              >
                Cash out
              </Button>
              <Button
                variant="secondary"
                size="sm"
                leadingIcon={<Vault className="size-4" aria-hidden="true" />}
                onClick={onOpenCashDrawer}
              >
                Open cash drawer
              </Button>
            </>
          )}
          <Button
            variant="secondary"
            size="sm"
            leadingIcon={<LogOut className="size-4" aria-hidden="true" />}
            onClick={onCloseSession}
          >
            Close session
          </Button>
          <Link
            to="/dashboard"
            className="inline-flex h-9 items-center gap-1.5 rounded-lg bg-danger px-3 text-sm font-semibold text-white transition-colors hover:bg-danger-strong focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
          >
            <LogOut className="size-4" aria-hidden="true" />
            Exit
          </Link>
        </div>
      </header>

      <div className="min-h-0 flex-1">{children}</div>
    </div>
  )
}
