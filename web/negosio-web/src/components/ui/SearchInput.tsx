import { Search, X } from 'lucide-react'
import { cn } from '../../lib/cn'
import { inputClass } from './TextField'

interface SearchInputProps {
  value: string
  onChange: (value: string) => void
  placeholder?: string
  className?: string
}

export function SearchInput({ value, onChange, placeholder = 'Search', className }: SearchInputProps) {
  return (
    <div className={cn('relative', className)}>
      <Search
        className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
        aria-hidden="true"
      />
      <input
        type="search"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={placeholder}
        className={cn(inputClass, 'pl-9 pr-9')}
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange('')}
          aria-label="Clear search"
          className="absolute right-2.5 top-1/2 -translate-y-1/2 rounded p-1 text-text-muted hover:bg-surface-subtle"
        >
          <X className="size-4" aria-hidden="true" />
        </button>
      )}
    </div>
  )
}
