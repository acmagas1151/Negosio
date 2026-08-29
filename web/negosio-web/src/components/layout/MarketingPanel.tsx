import { BarChart3, Boxes, Building2, ShieldCheck, TrendingUp, Users } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { Brand } from './Brand'

const BENEFITS: { icon: LucideIcon; title: string; text: string }[] = [
  { icon: TrendingUp, title: 'Boost your sales', text: 'Track revenue and growth as it happens.' },
  { icon: Boxes, title: 'Inventory made simple', text: 'Know what is in stock across every branch.' },
  { icon: Users, title: 'Manage your team', text: 'Roles and permissions built for retail & F&B.' },
  { icon: Building2, title: 'Multi-branch ready', text: 'Add locations without adding complexity.' },
  { icon: ShieldCheck, title: 'Secure & reliable', text: 'Your business data stays yours, always.' },
]

/** Lightweight left panel shown on wide screens for /register and /login. */
export function MarketingPanel() {
  return (
    <aside className="hidden bg-auth-tint p-10 lg:flex lg:flex-col lg:justify-between">
      <div className="space-y-8">
        <Brand />
        <div className="space-y-3">
          <p className="inline-flex items-center gap-1.5 rounded-full bg-white px-3 py-1 text-xs font-semibold text-primary-700 shadow-card">
            <BarChart3 className="size-3.5" aria-hidden="true" />
            Business management platform
          </p>
          <h2 className="text-2xl font-bold text-text-primary">
            All-in-one business management
          </h2>
          <p className="max-w-sm text-sm text-text-secondary">
            Manage sales, inventory, staff, orders and more from one platform built for
            retail and food &amp; beverage businesses.
          </p>
        </div>

        <ul className="space-y-4">
          {BENEFITS.map(({ icon: Icon, title, text }) => (
            <li key={title} className="flex gap-3">
              <span className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary-50 text-primary-600">
                <Icon className="size-4" aria-hidden="true" />
              </span>
              <div>
                <p className="text-sm font-semibold text-text-primary">{title}</p>
                <p className="text-[13px] text-text-secondary">{text}</p>
              </div>
            </li>
          ))}
        </ul>
      </div>

      <p className="text-xs text-text-muted">
        &copy; {new Date().getFullYear()} Negosio. All rights reserved.
      </p>
    </aside>
  )
}
