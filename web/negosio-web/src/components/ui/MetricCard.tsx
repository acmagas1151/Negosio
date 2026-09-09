import type { LucideIcon } from 'lucide-react'
import { TrendingDown, TrendingUp } from 'lucide-react'
import { cn } from '../../lib/cn'
import { Card } from './Card'

export type MetricAccent = 'blue' | 'green' | 'orange' | 'purple' | 'amber' | 'red'

const ACCENTS: Record<MetricAccent, string> = {
  blue: 'bg-primary-50 text-primary-600',
  green: 'bg-success-light text-success',
  orange: 'bg-orange-light text-orange',
  purple: 'bg-purple-light text-purple',
  amber: 'bg-warning-light text-warning',
  red: 'bg-danger-light text-danger',
}

interface MetricCardProps {
  icon: LucideIcon
  label: string
  value: string | number
  accent?: MetricAccent
  /** Optional period-over-period change, e.g. 12.5 or -3.2. */
  deltaPct?: number
  /** Trailing text after the delta, e.g. "vs previous period". Defaults to "vs last week". */
  deltaLabel?: string
  loading?: boolean
}

export function MetricCard({
  icon: Icon,
  label,
  value,
  accent = 'blue',
  deltaPct,
  deltaLabel = 'vs last week',
  loading = false,
}: MetricCardProps) {
  const up = (deltaPct ?? 0) >= 0
  return (
    <Card padding="md" className="flex flex-col gap-3">
      <span
        className={cn('flex size-10 items-center justify-center rounded-lg', ACCENTS[accent])}
      >
        <Icon className="size-5" aria-hidden="true" />
      </span>

      {loading ? (
        <div className="space-y-2">
          <div className="h-6 w-24 animate-pulse rounded bg-border-light" />
          <div className="h-3.5 w-16 animate-pulse rounded bg-border-light" />
        </div>
      ) : (
        <div className="space-y-0.5">
          <p className="truncate text-2xl font-bold text-text-primary">{value}</p>
          <p className="text-[13px] font-medium text-text-secondary">{label}</p>
        </div>
      )}

      {!loading && deltaPct !== undefined && (
        <p
          className={cn(
            'inline-flex items-center gap-1 text-[13px] font-semibold',
            up ? 'text-success' : 'text-danger',
          )}
        >
          {up ? (
            <TrendingUp className="size-3.5" aria-hidden="true" />
          ) : (
            <TrendingDown className="size-3.5" aria-hidden="true" />
          )}
          {up ? '+' : ''}
          {deltaPct}% <span className="font-normal text-text-muted">{deltaLabel}</span>
        </p>
      )}
    </Card>
  )
}
