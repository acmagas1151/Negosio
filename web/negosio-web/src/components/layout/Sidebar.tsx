import { NavLink } from 'react-router-dom'
import { cn } from '../../lib/cn'
import { NAV_ITEMS } from '../../lib/nav'
import { Badge } from '../ui'
import { Brand } from './Brand'

interface SidebarProps {
  /** Called when a nav link is followed (used to close the mobile drawer). */
  onNavigate?: () => void
}

export function Sidebar({ onNavigate }: SidebarProps) {
  return (
    <div className="flex h-full flex-col gap-6 border-r border-border bg-surface px-4 py-5">
      <div className="px-2">
        <Brand size="sm" />
      </div>

      <nav className="flex-1" aria-label="Main">
        <ul className="space-y-1">
          {NAV_ITEMS.map((item) => {
            const Icon = item.icon

            if (!item.enabled || !item.to) {
              return (
                <li key={item.label}>
                  <span
                    aria-disabled="true"
                    className="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-text-muted"
                  >
                    <Icon className="size-[18px]" aria-hidden="true" />
                    <span className="flex-1">{item.label}</span>
                    <Badge tone="neutral">Soon</Badge>
                  </span>
                </li>
              )
            }

            return (
              <li key={item.label}>
                <NavLink
                  to={item.to}
                  onClick={onNavigate}
                  className={({ isActive }) =>
                    cn(
                      'relative flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors',
                      isActive
                        ? 'bg-primary-50 text-primary-700'
                        : 'text-text-secondary hover:bg-surface-subtle hover:text-text-primary',
                    )
                  }
                >
                  {({ isActive }) => (
                    <>
                      {isActive && (
                        <span
                          className="absolute inset-y-1 left-0 w-1 rounded-r bg-primary-600"
                          aria-hidden="true"
                        />
                      )}
                      <Icon
                        className={cn('size-[18px]', isActive ? 'text-primary-600' : 'text-text-muted')}
                        aria-hidden="true"
                      />
                      {item.label}
                    </>
                  )}
                </NavLink>
              </li>
            )
          })}
        </ul>
      </nav>

      <p className="px-3 text-xs text-text-muted">Phase 1 · SaaS foundation</p>
    </div>
  )
}
