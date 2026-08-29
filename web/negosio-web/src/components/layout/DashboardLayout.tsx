import { useEffect, useId, useState } from 'react'
import type { ReactNode } from 'react'
import { X } from 'lucide-react'
import { cn } from '../../lib/cn'
import { DashboardHeader } from './DashboardHeader'
import { Sidebar } from './Sidebar'

interface DashboardLayoutProps {
  title: string
  children: ReactNode
}

export function DashboardLayout({ title, children }: DashboardLayoutProps) {
  const [navOpen, setNavOpen] = useState(false)
  const navId = useId()

  useEffect(() => {
    if (!navOpen) return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setNavOpen(false)
    }
    document.body.style.overflow = 'hidden'
    window.addEventListener('keydown', onKey)
    return () => {
      document.body.style.overflow = ''
      window.removeEventListener('keydown', onKey)
    }
  }, [navOpen])

  return (
    <div className="min-h-screen bg-background lg:grid lg:grid-cols-[260px_1fr]">
      {/* Desktop sidebar */}
      <aside className="hidden lg:block" id={navId}>
        <div className="sticky top-0 h-screen">
          <Sidebar />
        </div>
      </aside>

      {/* Mobile drawer */}
      {navOpen && (
        <div className="fixed inset-0 z-50 lg:hidden">
          <button
            type="button"
            aria-label="Close navigation"
            onClick={() => setNavOpen(false)}
            className="absolute inset-0 bg-text-primary/40"
          />
          <div
            role="dialog"
            aria-modal="true"
            aria-label="Navigation"
            className={cn(
              'drawer-in absolute inset-y-0 left-0 w-[264px] max-w-[80vw] bg-surface shadow-card-lg',
            )}
          >
            <button
              type="button"
              onClick={() => setNavOpen(false)}
              aria-label="Close navigation"
              className="absolute right-3 top-4 rounded-lg p-1.5 text-text-secondary hover:bg-surface-subtle"
            >
              <X className="size-5" aria-hidden="true" />
            </button>
            <Sidebar onNavigate={() => setNavOpen(false)} />
          </div>
        </div>
      )}

      <div className="flex min-w-0 flex-col">
        <DashboardHeader title={title} onOpenNav={() => setNavOpen(true)} navId={navId} />
        <main className="mx-auto w-full max-w-6xl flex-1 px-4 py-6 sm:px-6 sm:py-8">
          {children}
        </main>
      </div>
    </div>
  )
}
