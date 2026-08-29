import { Bell, LogOut, Menu, Search } from 'lucide-react'
import { useAuth } from '../../auth/AuthContext'

interface DashboardHeaderProps {
  title: string
  /** Opens the mobile navigation drawer. */
  onOpenNav: () => void
  navId: string
}

function initials(first?: string, last?: string) {
  return `${first?.[0] ?? ''}${last?.[0] ?? ''}`.toUpperCase() || 'U'
}

export function DashboardHeader({ title, onOpenNav, navId }: DashboardHeaderProps) {
  const { user, logout } = useAuth()

  return (
    <header className="sticky top-0 z-30 flex h-16 items-center gap-3 border-b border-border bg-surface px-4 sm:px-6">
      <button
        type="button"
        onClick={onOpenNav}
        aria-label="Open navigation"
        aria-controls={navId}
        aria-expanded={false}
        className="rounded-lg p-2 text-text-secondary hover:bg-surface-subtle lg:hidden"
      >
        <Menu className="size-5" aria-hidden="true" />
      </button>

      <h1 className="text-base font-bold text-text-primary sm:text-lg">{title}</h1>

      <div className="relative ml-auto hidden md:block">
        <Search
          className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
          aria-hidden="true"
        />
        <input
          type="search"
          placeholder="Search…"
          aria-label="Search"
          className="h-9 w-56 rounded-lg border border-border bg-surface-subtle pl-9 pr-3 text-sm text-text-primary placeholder:text-text-muted focus:border-primary-500 focus:bg-white focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
        />
      </div>

      <button
        type="button"
        aria-label="Notifications"
        className="ml-auto rounded-lg p-2 text-text-secondary hover:bg-surface-subtle md:ml-0"
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

        <div className="absolute right-0 mt-2 w-52 rounded-xl border border-border bg-surface p-1 shadow-popover">
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
    </header>
  )
}
