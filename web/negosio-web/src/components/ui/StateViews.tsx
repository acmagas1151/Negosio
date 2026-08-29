import type { ReactNode } from 'react'
import { AlertCircle, Loader2 } from 'lucide-react'
import { cn } from '../../lib/cn'
import { Button } from './Button'
import { Card } from './Card'

export function LoadingState({ label = 'Loading…' }: { label?: string }) {
  return (
    <div className="flex min-h-[40vh] flex-col items-center justify-center gap-3 text-text-muted">
      <Loader2 className="size-6 animate-spin text-primary-500" aria-hidden="true" />
      <p className="text-sm" role="status">
        {label}
      </p>
    </div>
  )
}

export function SkeletonText({ className }: { className?: string }) {
  return <div className={cn('h-4 animate-pulse rounded bg-border-light', className)} />
}

export function SkeletonCard() {
  return (
    <Card padding="md" className="flex flex-col gap-3">
      <div className="size-10 animate-pulse rounded-lg bg-border-light" />
      <SkeletonText className="w-24" />
      <SkeletonText className="h-3.5 w-16" />
    </Card>
  )
}

interface ErrorStateProps {
  title?: string
  message?: ReactNode
  onRetry?: () => void
  className?: string
}

export function ErrorState({
  title = 'Something went wrong',
  message,
  onRetry,
  className,
}: ErrorStateProps) {
  return (
    <Card
      padding="lg"
      className={cn('flex flex-col items-center gap-3 text-center', className)}
    >
      <span className="flex size-11 items-center justify-center rounded-full bg-danger-light text-danger">
        <AlertCircle className="size-5" aria-hidden="true" />
      </span>
      <div className="space-y-1">
        <p className="text-sm font-semibold text-text-primary">{title}</p>
        {message && <p className="text-[13px] text-text-secondary">{message}</p>}
      </div>
      {onRetry && (
        <Button variant="secondary" size="sm" onClick={onRetry}>
          Try again
        </Button>
      )}
    </Card>
  )
}
