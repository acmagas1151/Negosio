import type { TextareaHTMLAttributes } from 'react'
import { cn } from '../../lib/cn'
import { FormField } from './FormField'

interface TextAreaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  label: string
  error?: string
  hint?: string
}

const textareaClass =
  'w-full rounded-lg border border-border-strong bg-white px-3 py-2 text-sm text-text-primary min-h-[80px] ' +
  'placeholder:text-text-muted transition-colors ' +
  'focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15 ' +
  'disabled:cursor-not-allowed disabled:bg-surface-subtle aria-[invalid=true]:border-danger ' +
  'aria-[invalid=true]:focus:ring-danger/15'

export function TextArea({ label, error, hint, id, className, ...textareaProps }: TextAreaProps) {
  const fieldId = id ?? textareaProps.name

  return (
    <FormField htmlFor={fieldId} label={label} error={error} hint={hint}>
      <textarea
        id={fieldId}
        aria-invalid={error ? true : undefined}
        aria-describedby={
          fieldId ? (error ? `${fieldId}-error` : hint ? `${fieldId}-hint` : undefined) : undefined
        }
        className={cn(textareaClass, className)}
        {...textareaProps}
      />
    </FormField>
  )
}
