import { NavLink } from 'react-router-dom'
import { cn } from '../../lib/cn'
import { useCan } from '../../lib/useCan'

interface TabDef {
  label: string
  to: string
}

/**
 * Lateral navigation between the settings pages (`/settings/tax`, `/settings/receipts`).
 * The top-level access points are the sidebar entries in `nav.ts`; this bar is for moving
 * between them once you're on a settings page.
 */
export function SettingsTabs() {
  const canReceiptSettings = useCan('receipt:settings')

  const tabs: TabDef[] = [
    { label: 'Tax', to: '/settings/tax' },
    ...(canReceiptSettings ? [{ label: 'Receipts', to: '/settings/receipts' }] : []),
  ]

  return (
    <div className="border-b border-border">
      <nav className="-mb-px flex gap-6" aria-label="Settings sections">
        {tabs.map((tab) => (
          <NavLink
            key={tab.to}
            to={tab.to}
            className={({ isActive }) =>
              cn(
                'border-b-2 px-1 pb-3 text-sm font-semibold transition-colors',
                isActive
                  ? 'border-primary-500 text-text-primary'
                  : 'border-transparent text-text-muted hover:text-text-primary',
              )
            }
          >
            {tab.label}
          </NavLink>
        ))}
      </nav>
    </div>
  )
}
