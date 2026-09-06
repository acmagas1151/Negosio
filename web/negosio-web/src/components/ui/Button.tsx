import type { ButtonHTMLAttributes, ReactNode } from 'react'
import { Loader2 } from 'lucide-react'
import { cn } from '../../lib/cn'

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'destructive'
type ButtonSize = 'sm' | 'md' | 'lg'

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: ButtonSize
  loading?: boolean
  block?: boolean
  leadingIcon?: ReactNode
  trailingIcon?: ReactNode
}

const VARIANTS: Record<ButtonVariant, string> = {
  primary:
    'bg-primary-600 text-white hover:bg-primary-700 disabled:hover:bg-primary-600',
  secondary:
    'bg-white text-text-secondary border border-border-strong hover:bg-surface-subtle hover:text-text-primary',
  ghost:
    'bg-transparent text-text-secondary hover:bg-surface-subtle hover:text-text-primary',
  destructive:
    'bg-danger text-white hover:bg-danger-strong disabled:hover:bg-danger',
}

const SIZES: Record<ButtonSize, string> = {
  sm: 'h-9 px-3 text-sm gap-1.5',
  md: 'h-11 px-5 text-sm gap-2',
  lg: 'h-14 px-6 text-base gap-2.5',
}

export function Button({
  variant = 'primary',
  size = 'md',
  loading = false,
  block = false,
  leadingIcon,
  trailingIcon,
  className,
  children,
  disabled,
  type = 'button',
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      className={cn(
        'inline-flex items-center justify-center rounded-lg font-semibold',
        'transition-colors focus-visible:outline-2 focus-visible:outline-offset-2',
        'focus-visible:outline-primary-500 disabled:cursor-not-allowed disabled:opacity-60',
        VARIANTS[variant],
        SIZES[size],
        block && 'w-full',
        className,
      )}
      {...rest}
    >
      {loading ? (
        <Loader2 className="size-4 animate-spin" aria-hidden="true" />
      ) : (
        leadingIcon
      )}
      {children}
      {!loading && trailingIcon}
    </button>
  )
}
