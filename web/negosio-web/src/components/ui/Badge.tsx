import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'

type BadgeTone = 'neutral' | 'blue' | 'success' | 'warning' | 'danger' | 'purple'

const TONES: Record<BadgeTone, string> = {
  neutral: 'bg-surface-subtle text-text-secondary border-border',
  blue: 'bg-primary-50 text-primary-700 border-primary-100',
  success: 'bg-success-light text-success-strong border-success/20',
  warning: 'bg-warning-light text-warning-strong border-warning/20',
  danger: 'bg-danger-light text-danger-strong border-danger/20',
  purple: 'bg-purple-light text-purple border-purple/20',
}

export function Badge({
  tone = 'neutral',
  children,
  className,
}: {
  tone?: BadgeTone
  children: ReactNode
  className?: string
}) {
  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-full border px-2 py-0.5',
        'text-[11px] font-semibold uppercase tracking-wide',
        TONES[tone],
        className,
      )}
    >
      {children}
    </span>
  )
}
