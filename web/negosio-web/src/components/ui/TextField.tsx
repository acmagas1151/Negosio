import type { InputHTMLAttributes } from 'react'
import { cn } from '../../lib/cn'
import { FormField } from './FormField'

interface TextFieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string
  error?: string
  hint?: string
}

export const inputClass =
  'h-11 w-full rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary ' +
  'placeholder:text-text-muted transition-colors ' +
  'focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15 ' +
  'disabled:cursor-not-allowed disabled:bg-surface-subtle aria-[invalid=true]:border-danger ' +
  'aria-[invalid=true]:focus:ring-danger/15'

export function TextField({ label, error, hint, id, className, ...inputProps }: TextFieldProps) {
  const fieldId = id ?? inputProps.name

  return (
    <FormField htmlFor={fieldId} label={label} error={error} hint={hint}>
      <input
        id={fieldId}
        aria-invalid={error ? true : undefined}
        aria-describedby={
          fieldId ? (error ? `${fieldId}-error` : hint ? `${fieldId}-hint` : undefined) : undefined
        }
        className={cn(inputClass, className)}
        {...inputProps}
      />
    </FormField>
  )
}
