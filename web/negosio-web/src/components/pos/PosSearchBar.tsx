import { forwardRef } from 'react'
import { ScanLine, Search } from 'lucide-react'
import { cn } from '../../lib/cn'
import { inputClass } from '../ui/TextField'

interface Props {
  value: string
  onChange: (value: string) => void
  onEnter: (value: string) => void
}

/** POS product search. Enter triggers a barcode-first lookup (see PosTerminal). */
export const PosSearchBar = forwardRef<HTMLInputElement, Props>(function PosSearchBar(
  { value, onChange, onEnter },
  ref,
) {
  return (
    <div className="relative">
      <Search
        className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
        aria-hidden="true"
      />
      <input
        ref={ref}
        type="text"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Enter') {
            e.preventDefault()
            onEnter(value.trim())
          }
        }}
        placeholder="Search by name, SKU or barcode — or scan a product"
        aria-label="Search products"
        autoFocus
        className={cn(inputClass, 'h-12 pl-9 pr-10 text-base')}
      />
      <ScanLine
        className="pointer-events-none absolute right-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
        aria-hidden="true"
      />
    </div>
  )
})
