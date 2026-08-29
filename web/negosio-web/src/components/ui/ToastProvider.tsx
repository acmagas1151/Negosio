import { useCallback, useMemo, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import { AlertTriangle, CheckCircle2, Info, X, XCircle } from 'lucide-react'
import { cn } from '../../lib/cn'
import { ToastContext } from './use-toast'
import type { ToastItem, ToastTone } from './use-toast'

const TONE_STYLES: Record<ToastTone, { bar: string; icon: typeof Info }> = {
  success: { bar: 'text-success', icon: CheckCircle2 },
  warning: { bar: 'text-warning', icon: AlertTriangle },
  error: { bar: 'text-danger', icon: XCircle },
  info: { bar: 'text-primary-600', icon: Info },
}

const AUTO_DISMISS_MS = 5000

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<ToastItem[]>([])
  const counter = useRef(0)

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((t) => t.id !== id))
  }, [])

  const toast = useCallback(
    (tone: ToastTone, message: string) => {
      counter.current += 1
      const id = counter.current
      setToasts((current) => [...current, { id, tone, message }])
      window.setTimeout(() => dismiss(id), AUTO_DISMISS_MS)
      return id
    },
    [dismiss],
  )

  const value = useMemo(() => ({ toast, dismiss }), [toast, dismiss])

  return (
    <ToastContext value={value}>
      {children}
      <div
        className="pointer-events-none fixed inset-x-0 top-0 z-[100] flex flex-col items-center gap-2 p-4 sm:items-end"
        aria-live="polite"
      >
        {toasts.map(({ id, tone, message }) => {
          const { bar, icon: Icon } = TONE_STYLES[tone]
          return (
            <div
              key={id}
              role="status"
              className={cn(
                'pointer-events-auto flex w-full max-w-sm items-start gap-3 rounded-xl border border-border',
                'bg-surface px-4 py-3 text-sm text-text-primary shadow-popover',
              )}
            >
              <Icon className={cn('mt-0.5 size-4 shrink-0', bar)} aria-hidden="true" />
              <p className="flex-1">{message}</p>
              <button
                type="button"
                onClick={() => dismiss(id)}
                className="shrink-0 rounded p-0.5 text-text-muted hover:text-text-primary"
                aria-label="Dismiss notification"
              >
                <X className="size-4" aria-hidden="true" />
              </button>
            </div>
          )
        })}
      </div>
    </ToastContext>
  )
}
