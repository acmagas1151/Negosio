import { NavLink } from 'react-router-dom'
import { BarChart3, Bell, LogOut, Package, Search, Settings, ShoppingCart, ClipboardList } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import { useAuth } from '../../auth/AuthContext'
import { useCapabilities } from '../../lib/useCan'
import { Badge } from '../ui'
import { Brand } from '../layout/Brand'
import { cn } from '../../lib/cn'

interface NavEntry {
  label: string
  icon: LucideIcon
  to?: string
  capability?: 'sales:view' | 'settings:write'
}

const NAV_ENTRIES: NavEntry[] = [
  { label: 'POS', icon: ShoppingCart, to: '/pos' },
  { label: 'Sales', icon: ClipboardList, to: '/sales', capability: 'sales:view' },
  { label: 'Products', icon: Package, to: '/products' },
  { label: 'Reports', icon: BarChart3 }, // no route yet — shown disabled, not a fake link
  { label: 'Settings', icon: Settings, to: '/settings', capability: 'settings:write' },
]

function initials(first?: string, last?: string) {
  return `${first?.[0] ?? ''}${last?.[0] ?? ''}`.toUpperCase() || 'U'
}

/** The POS terminal's own compact top bar — POS intentionally skips the app's sidebar layout
 * (DashboardLayout), so this is its equivalent of DashboardHeader, not a duplicate of it. */
export function PosTopNav() {
  const { user, logout } = useAuth()
  const can = useCapabilities()

  const entries = NAV_ENTRIES.filter((e) => !e.capability || can(e.capability))

  return (
    <header className="flex h-14 shrink-0 items-center gap-4 border-b border-border bg-surface px-4">
      <Brand size="sm" />

      <nav className="flex items-center gap-1" aria-label="Main">
        {entries.map((entry) => {
          const Icon = entry.icon
          if (!entry.to) {
            return (
              <span
                key={entry.label}
                aria-disabled="true"
                className="flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-medium text-text-muted"
              >
                <Icon className="size-4" aria-hidden="true" />
                {entry.label}
                <Badge tone="neutral">Soon</Badge>
              </span>
            )
          }
          return (
            <NavLink
              key={entry.label}
              to={entry.to}
              className={({ isActive }) =>
                cn(
                  'flex items-center gap-1.5 rounded-lg px-3 py-1.5 text-sm font-semibold transition-colors',
                  isActive
                    ? 'bg-primary-50 text-primary-700'
                    : 'text-text-secondary hover:bg-surface-subtle hover:text-text-primary',
                )
              }
            >
              <Icon className="size-4" aria-hidden="true" />
              {entry.label}
            </NavLink>
          )
        })}
      </nav>

      <div className="ml-auto flex items-center gap-2">
        {/* Visual only — no global search index exists yet (same non-wired pattern already used
            by DashboardHeader's search box). */}
        <div className="relative hidden lg:block">
          <Search
            className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
            aria-hidden="true"
          />
          <input
            type="search"
            placeholder="Search…"
            aria-label="Search"
            disabled
            className="h-9 w-48 cursor-not-allowed rounded-lg border border-border bg-surface-subtle pl-9 pr-12 text-sm text-text-muted placeholder:text-text-muted"
          />
          <kbd className="pointer-events-none absolute right-2 top-1/2 -translate-y-1/2 rounded border border-border-strong bg-surface px-1.5 py-0.5 text-[10px] font-semibold text-text-muted">
            Ctrl K
          </kbd>
        </div>

        <button
          type="button"
          aria-label="Notifications"
          className="rounded-lg p-2 text-text-secondary hover:bg-surface-subtle"
        >
          <Bell className="size-5" aria-hidden="true" />
        </button>

        <details className="group relative">
          <summary className="flex cursor-pointer list-none items-center gap-2 rounded-lg p-1 hover:bg-surface-subtle [&::-webkit-details-marker]:hidden">
            <span className="flex size-8 items-center justify-center rounded-full bg-primary-600 text-xs font-bold text-white">
              {initials(user?.firstName, user?.lastName)}
            </span>
            <span className="hidden text-left sm:block">
              <span className="block text-sm font-semibold leading-tight text-text-primary">
                {user?.firstName} {user?.lastName}
              </span>
              <span className="block text-xs leading-tight text-text-muted">{user?.role}</span>
            </span>
          </summary>

          <div className="absolute right-0 z-20 mt-2 w-52 rounded-xl border border-border bg-surface p-1 shadow-popover">
            <div className="border-b border-border-light px-3 py-2">
              <p className="truncate text-sm font-semibold text-text-primary">
                {user?.firstName} {user?.lastName}
              </p>
              <p className="truncate text-xs text-text-muted">{user?.email}</p>
            </div>
            <button
              type="button"
              onClick={logout}
              className="mt-1 flex w-full items-center gap-2 rounded-lg px-3 py-2 text-sm font-medium text-text-secondary hover:bg-surface-subtle hover:text-text-primary"
            >
              <LogOut className="size-4" aria-hidden="true" />
              Sign out
            </button>
          </div>
        </details>
      </div>
    </header>
  )
}
