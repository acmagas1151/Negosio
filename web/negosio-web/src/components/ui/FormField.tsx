import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'

interface FormFieldProps {
  /** id of the control this field wraps — used for the <label htmlFor>. */
  htmlFor?: string
  label: string
  error?: string
  hint?: string
  className?: string
  children: ReactNode
}

/** Shared label + hint + error shell for text inputs, selects, etc. */
export function FormField({ htmlFor, label, error, hint, className, children }: FormFieldProps) {
  const describedById = htmlFor
    ? error
      ? `${htmlFor}-error`
      : hint
        ? `${htmlFor}-hint`
        : undefined
    : undefined

  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={htmlFor} className="text-sm font-semibold text-text-secondary">
        {label}
      </label>
      {children}
      {/* Reserve a line so validation errors never shift the layout. */}
      <p
        id={describedById}
        className={cn(
          'min-h-[1rem] text-[13px] leading-4',
          error ? 'text-danger' : 'text-text-muted',
        )}
      >
        {error ?? hint ?? ' '}
      </p>
    </div>
  )
}
