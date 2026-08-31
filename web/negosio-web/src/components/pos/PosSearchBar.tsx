import { Search } from 'lucide-react'
import { cn } from '../../lib/cn'
import { inputClass } from '../ui/TextField'

interface Props {
  value: string
  onChange: (value: string) => void
  onEnter: (value: string) => void
}

/** POS product search. Enter triggers a barcode-first lookup (see PosTerminal). */
export function PosSearchBar({ value, onChange, onEnter }: Props) {
  return (
    <div className="relative">
      <Search
        className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
        aria-hidden="true"
      />
      <input
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault()
            onEnter(value.trim())
          }
        }}
        placeholder="Search by name, SKU or barcode — or scan"
        aria-label="Search products"
        autoFocus
        className={cn(inputClass, 'h-12 pl-9 text-base')}
      />
    </div>
  )
}
