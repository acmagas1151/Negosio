import type { ReactNode } from 'react'
import { Brand } from './Brand'
import { MarketingPanel } from './MarketingPanel'

interface AuthLayoutProps {
  /** Short heading above the form, e.g. "Create your business". */
  title: string
  subtitle?: ReactNode
  children: ReactNode
  /** Footer line under the form (e.g. the "already have an account" link). */
  footer?: ReactNode
  /** "md" for the sign-in form, "lg" for the multi-step wizard. */
  width?: 'md' | 'lg'
}

export function AuthLayout({ title, subtitle, children, footer, width = 'md' }: AuthLayoutProps) {
  const contentWidth = width === 'lg' ? 'max-w-2xl' : 'max-w-md'
  return (
    <div className="bg-auth-tint flex min-h-screen items-center justify-center p-4 sm:p-6">
      <div className="w-full max-w-[1100px] overflow-hidden rounded-2xl border border-border bg-surface shadow-card-lg lg:grid lg:grid-cols-[minmax(340px,400px)_1fr]">
        <MarketingPanel />

        <div className="flex min-w-0 flex-col justify-center p-6 sm:p-10 lg:p-12">
          <div className={`mx-auto w-full min-w-0 ${contentWidth}`}>
            <div className="lg:hidden">
              <Brand size="sm" />
            </div>
            <header className="mt-6 space-y-1 lg:mt-0">
              <h1 className="text-2xl font-bold text-text-primary">{title}</h1>
              {subtitle && <p className="text-sm text-text-secondary">{subtitle}</p>}
            </header>

            <div className="mt-6">{children}</div>

            {footer && (
              <p className="mt-6 text-center text-sm text-text-secondary">{footer}</p>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
