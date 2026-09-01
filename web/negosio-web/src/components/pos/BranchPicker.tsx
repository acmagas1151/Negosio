import { Link } from 'react-router-dom'
import { Building2 } from 'lucide-react'
import type { PosBranchDto } from '../../api/types'
import { Button } from '../ui'

interface Props {
  branches: PosBranchDto[]
  onPick: (branchId: string) => void
}

/** Owner/Admin only — choose which branch's till to operate. Branch-scoped users never see this. */
export function BranchPicker({ branches, onPick }: Props) {
  return (
    <div className="w-full max-w-sm space-y-4 rounded-2xl border border-border bg-surface p-6 shadow-card-lg">
      <div className="flex items-center gap-2">
        <span className="flex size-9 items-center justify-center rounded-full bg-primary-50 text-primary-700">
          <Building2 className="size-4.5" aria-hidden="true" />
        </span>
        <h1 className="text-lg font-bold text-text-primary">Choose a branch</h1>
      </div>
      <ul className="space-y-2">
        {branches.map((b) => (
          <li key={b.id}>
            <Button block variant="secondary" onClick={() => onPick(b.id)} className="justify-between">
              <span>{b.name}</span>
              <span className="text-text-muted">{b.code}</span>
            </Button>
          </li>
        ))}
      </ul>
      <Link
        to="/dashboard"
        className="block text-center text-[13px] font-semibold text-text-muted hover:underline"
      >
        Exit POS
      </Link>
    </div>
  )
}
