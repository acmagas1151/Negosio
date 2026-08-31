import { useState } from 'react'
import { Link } from 'react-router-dom'
import { Calculator } from 'lucide-react'
import type { RegisterDto } from '../../api/types'
import { Button, EmptyState, Select } from '../ui'

interface Props {
  registers: RegisterDto[]
  onPick: (registerId: string) => void
}

export function RegisterPicker({ registers, onPick }: Props) {
  const [selected, setSelected] = useState('')

  if (registers.length === 0) {
    return (
      <EmptyState
        icon={Calculator}
        title="No active registers"
        description="Create or reactivate a register before you can take sales."
        action={
          <Link
            to="/registers"
            className="inline-flex h-9 items-center rounded-lg border border-border-strong bg-white px-3 text-sm font-semibold text-text-secondary hover:bg-surface-subtle"
          >
            Manage registers
          </Link>
        }
      />
    )
  }

  return (
    <div className="w-full max-w-sm space-y-4 rounded-2xl border border-border bg-surface p-6 shadow-card-lg">
      <div className="space-y-1">
        <h1 className="text-lg font-bold text-text-primary">Choose a register</h1>
        <p className="text-[13px] text-text-muted">
          This device will use the register you pick until you change it.
        </p>
      </div>
      <Select
        aria-label="Register"
        value={selected}
        onChange={(e) => setSelected(e.target.value)}
      >
        <option value="">Select a register</option>
        {registers.map((r) => (
          <option key={r.id} value={r.id}>
            {r.name} ({r.code})
          </option>
        ))}
      </Select>
      <Button block disabled={!selected} onClick={() => selected && onPick(selected)}>
        Continue
      </Button>
    </div>
  )
}
