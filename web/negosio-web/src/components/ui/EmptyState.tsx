import type { LucideIcon } from 'lucide-react'
import type { ReactNode } from 'react'
import { cn } from '../../lib/cn'

interface EmptyStateProps {
  icon?: LucideIcon
  title: string
  description?: ReactNode
  action?: ReactNode
  className?: string
}

export function EmptyState({ icon: Icon, title, description, action, className }: EmptyStateProps) {
  return (
    <div
      className={cn(
        'flex flex-col items-center gap-3 rounded-xl border border-dashed border-border',
        'bg-surface-subtle px-6 py-10 text-center',
        className,
      )}
    >
      {Icon && (
        <span className="flex size-11 items-center justify-center rounded-full bg-primary-50 text-primary-600">
          <Icon className="size-5" aria-hidden="true" />
        </span>
      )}
      <div className="space-y-1">
        <p className="text-sm font-semibold text-text-primary">{title}</p>
        {description && (
          <p className="mx-auto max-w-sm text-[13px] text-text-secondary">{description}</p>
        )}
      </div>
      {action}
    </div>
  )
}
