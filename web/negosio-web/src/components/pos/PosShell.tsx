import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { LogOut } from 'lucide-react'
import { useAuth } from '../../auth/AuthContext'
import type { RegisterSessionDto } from '../../api/types'
import { formatMoney } from '../../lib/format'
import { Button } from '../ui'

interface Props {
  session: RegisterSessionDto
  register: { id: string; name: string }
  onCloseSession: () => void
  children: ReactNode
}

/** Full-screen POS chrome — no dashboard sidebar. Slim top bar + body. */
export function PosShell({ session, register, onCloseSession, children }: Props) {
  const { user } = useAuth()

  return (
    <div className="flex h-screen flex-col bg-background">
      <header className="flex h-14 shrink-0 items-center justify-between gap-4 border-b border-border bg-surface px-4">
        <div className="flex items-baseline gap-2 truncate">
          <span className="truncate font-bold text-text-primary">{user?.tenantName}</span>
          <span className="text-text-muted">·</span>
          <span className="truncate font-semibold text-text-secondary">{register.name}</span>
        </div>
        <div className="flex items-center gap-3">
          <span className="hidden text-[13px] text-text-muted sm:inline">
            Open · {formatMoney(session.openingCash)}
            {user?.firstName ? ` · ${user.firstName}` : ''}
          </span>
          <Button variant="ghost" size="sm" onClick={onCloseSession}>
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
      </header>
      <div className="min-h-0 flex-1">{children}</div>
    </div>
  )
}
