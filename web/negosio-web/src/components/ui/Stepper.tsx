import { Check } from 'lucide-react'
import { cn } from '../../lib/cn'

interface StepperProps {
  steps: readonly string[]
  /** Zero-based index of the active step. */
  current: number
}

export function Stepper({ steps, current }: StepperProps) {
  return (
    <ol className="flex items-start" aria-label="Progress">
      {steps.map((label, index) => {
        const done = index < current
        const active = index === current
        const status = done ? 'completed' : active ? 'current' : 'upcoming'
        const first = index === 0
        const last = index === steps.length - 1

        return (
          <li
            key={label}
            className="flex flex-1 flex-col items-center"
            aria-current={active ? 'step' : undefined}
          >
            <div className="flex w-full items-center">
              <span
                className={cn(
                  'h-0.5 flex-1',
                  first && 'invisible',
                  index <= current ? 'bg-primary-500' : 'bg-border',
                )}
                aria-hidden="true"
              />
              <span
                className={cn(
                  'flex size-7 shrink-0 items-center justify-center rounded-full border text-xs font-bold transition-colors',
                  done && 'border-primary-600 bg-primary-600 text-white',
                  active && 'border-primary-500 bg-primary-50 text-primary-600',
                  !done && !active && 'border-border-strong bg-white text-text-muted',
                )}
              >
                {done ? <Check className="size-4" aria-hidden="true" /> : index + 1}
              </span>
              <span
                className={cn(
                  'h-0.5 flex-1',
                  last && 'invisible',
                  index < current ? 'bg-primary-500' : 'bg-border',
                )}
                aria-hidden="true"
              />
            </div>
            <span
              className={cn(
                'mt-2 hidden px-1 text-center text-xs font-medium sm:block',
                active ? 'text-text-primary' : 'text-text-muted',
              )}
            >
              {label}
              <span className="sr-only"> — {status}</span>
            </span>
          </li>
        )
      })}
    </ol>
  )
}
