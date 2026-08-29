import type { HTMLAttributes } from 'react'
import { cn } from '../../lib/cn'

interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /** Vertical rhythm inside the card. */
  padding?: 'sm' | 'md' | 'lg' | 'none'
  /** Drop the soft shadow (e.g. for nested / flat cards). */
  flat?: boolean
}

const PADDING = {
  none: '',
  sm: 'p-4',
  md: 'p-5',
  lg: 'p-6',
} as const

export function Card({ padding = 'md', flat = false, className, children, ...rest }: CardProps) {
  return (
    <div
      className={cn(
        'rounded-xl border border-border bg-surface',
        !flat && 'shadow-card',
        PADDING[padding],
        className,
      )}
      {...rest}
    >
      {children}
    </div>
  )
}
