import type { ReactNode } from 'react'
import { Link } from 'react-router-dom'
import { ShieldAlert } from 'lucide-react'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { useCan, type Capability } from '../lib/useCan'

/**
 * Page-level guard for routes whose backend policy would 403 the current role. Navigation already
 * hides these entries; this catches a manual URL. The backend remains the real authority.
 */
export function RequireCapability({
  capability,
  title = 'Page',
  children,
}: {
  capability: Capability
  title?: string
  children: ReactNode
}) {
  if (useCan(capability)) return <>{children}</>

  return (
    <DashboardLayout title={title}>
      <div className="mx-auto flex max-w-md flex-col items-center gap-3 rounded-xl border border-border bg-surface px-6 py-12 text-center shadow-card">
        <span className="flex size-11 items-center justify-center rounded-full bg-warning-light text-warning">
          <ShieldAlert className="size-5" aria-hidden="true" />
        </span>
        <div className="space-y-1">
          <p className="text-sm font-semibold text-text-primary">You don&rsquo;t have access to this page</p>
          <p className="text-[13px] text-text-secondary">
            Ask an owner or admin if you think you should.
          </p>
        </div>
        <Link to="/dashboard" className="text-[13px] font-semibold text-primary-700 hover:underline">
          Back to dashboard
        </Link>
      </div>
    </DashboardLayout>
  )
}
