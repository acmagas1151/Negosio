import { Link } from 'react-router-dom'
import { Calculator } from 'lucide-react'
import type { PosRegisterDto } from '../../api/types'
import { Button, Callout, EmptyState } from '../ui'

interface Props {
  registers: PosRegisterDto[]
  branchName?: string | null
  /** Open a fresh session on an available register (→ opening-cash screen). */
  onSelect: (registerId: string) => void
  /** Resume the caller's own open session on this register (→ straight to terminal). */
  onContinue: (registerId: string) => void
  /** Owner/Admin only — return to the branch picker. */
  onSwitchBranch?: () => void
  notice?: string | null
}

export function RegisterPicker({
  registers,
  branchName,
  onSelect,
  onContinue,
  onSwitchBranch,
  notice,
}: Props) {
  if (registers.length === 0) {
    return (
      <div className="w-full max-w-sm space-y-4 rounded-2xl border border-border bg-surface p-6 text-center shadow-card-lg">
        <EmptyState
          icon={Calculator}
          title="No active registers"
          description={
            branchName
              ? `${branchName} has no active register. Ask an administrator to add one.`
              : 'Ask an administrator to add a register for your branch.'
          }
        />
        <Link to="/dashboard" className="text-[13px] font-semibold text-primary-700 hover:underline">
          Exit POS
        </Link>
      </div>
    )
  }

  return (
    <div className="w-full max-w-sm space-y-4 rounded-2xl border border-border bg-surface p-6 shadow-card-lg">
      <div className="space-y-1">
        <h1 className="text-lg font-bold text-text-primary">Choose a register</h1>
        {branchName && <p className="text-[13px] text-text-muted">{branchName}</p>}
      </div>

      {notice && <Callout tone="warning">{notice}</Callout>}

      <ul className="space-y-2">
        {registers.map((r) => {
          const open = r.openSession
          const mine = open?.mine ?? false
          return (
            <li
              key={r.id}
              className="flex items-center justify-between gap-3 rounded-xl border border-border px-3 py-2.5"
            >
              <div className="min-w-0">
                <p className="truncate font-semibold text-text-primary">
                  {r.name} <span className="font-normal text-text-muted">({r.code})</span>
                </p>
                <p className="text-[12px] text-text-muted">
                  {mine
                    ? 'Your open session'
                    : open
                      ? `In use by ${open.openedByName}`
                      : 'Available'}
                </p>
              </div>
              {mine ? (
                <Button size="sm" onClick={() => onContinue(r.id)}>
                  Continue
                </Button>
              ) : open ? (
                <Button size="sm" variant="secondary" disabled>
                  In use
                </Button>
              ) : (
                <Button size="sm" variant="secondary" onClick={() => onSelect(r.id)}>
                  Select
                </Button>
              )}
            </li>
          )
        })}
      </ul>

      <div className="flex items-center justify-between border-t border-border pt-3 text-[13px]">
        {onSwitchBranch ? (
          <button
            type="button"
            onClick={onSwitchBranch}
            className="font-semibold text-primary-700 hover:underline"
          >
            ← Switch branch
          </button>
        ) : (
          <span />
        )}
        <Link to="/dashboard" className="font-semibold text-text-muted hover:underline">
          Exit POS
        </Link>
      </div>
    </div>
  )
}
