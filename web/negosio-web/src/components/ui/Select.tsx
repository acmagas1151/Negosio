import type { SelectHTMLAttributes } from 'react'
import { ChevronDown } from 'lucide-react'
import { cn } from '../../lib/cn'
import { FormField } from './FormField'
import { inputClass } from './TextField'

interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {
  label: string
  error?: string
  hint?: string
}

export function Select({ label, error, hint, id, className, children, ...selectProps }: SelectProps) {
  const fieldId = id ?? selectProps.name

  return (
    <FormField htmlFor={fieldId} label={label} error={error} hint={hint}>
      <div className="relative">
        <select
          id={fieldId}
          aria-invalid={error ? true : undefined}
          className={cn(inputClass, 'appearance-none pr-9', className)}
          {...selectProps}
        >
          {children}
        </select>
        <ChevronDown
          className="pointer-events-none absolute right-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
          aria-hidden="true"
        />
      </div>
    </FormField>
  )
}
