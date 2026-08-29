import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, XCircle } from 'lucide-react'
import { cn } from '../../lib/cn'

type CalloutTone = 'error' | 'info' | 'success' | 'warning'

const TONES: Record<CalloutTone, { wrap: string; icon: typeof Info }> = {
  error: { wrap: 'bg-danger-light text-danger-strong border-danger/20', icon: XCircle },
  warning: { wrap: 'bg-warning-light text-warning-strong border-warning/20', icon: AlertTriangle },
  success: { wrap: 'bg-success-light text-success-strong border-success/20', icon: CheckCircle2 },
  info: { wrap: 'bg-primary-50 text-primary-700 border-primary-100', icon: Info },
}

export function Callout({
  tone = 'error',
  children,
}: {
  tone?: CalloutTone
  children: ReactNode
}) {
  const { wrap, icon: Icon } = TONES[tone]
  return (
    <div
      role={tone === 'error' ? 'alert' : 'status'}
      className={cn(
        'mb-4 flex items-start gap-2.5 rounded-lg border px-3.5 py-3 text-sm',
        wrap,
      )}
    >
      <Icon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      <div className="[&_a]:font-semibold [&_a]:underline">{children}</div>
    </div>
  )
}
