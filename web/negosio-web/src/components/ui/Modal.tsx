import type { KeyboardEvent, ReactNode } from 'react'
import { useCallback, useEffect, useId, useRef } from 'react'
import { createPortal } from 'react-dom'
import { X } from 'lucide-react'
import { cn } from '../../lib/cn'

interface ModalProps {
  open: boolean
  onClose: () => void
  title: string
  children: ReactNode
  footer?: ReactNode
  size?: 'sm' | 'md'
  /** Keeps `title` as the dialog's accessible name but doesn't render it as visible header text —
   * for content (like a success confirmation) that wants its own bespoke heading in the body
   * instead of the standard title-bar row. The close button still renders, alone, top-right. */
  hideTitle?: boolean
}

const FOCUSABLE =
  'a[href],button:not([disabled]),textarea:not([disabled]),input:not([disabled]),select:not([disabled]),[tabindex]:not([tabindex="-1"])'
// FOCUSABLE is a comma-separated selector list — appending `:not(...)` to the end of the whole
// string would only exclude it from the last alternative, not every one, so it's distributed
// across each branch here instead.
const FOCUSABLE_EXCEPT_CLOSE = FOCUSABLE.split(',')
  .map((s) => `${s}:not([data-modal-close])`)
  .join(',')

export function Modal({ open, onClose, title, children, footer, size = 'md', hideTitle = false }: ModalProps) {
  const panelRef = useRef<HTMLDivElement>(null)
  const titleId = useId()
  const restoreFocusRef = useRef<HTMLElement | null>(null)

  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation()
        onClose()
        return
      }
      if (e.key !== 'Tab' || !panelRef.current) return
      const items = Array.from(panelRef.current.querySelectorAll<HTMLElement>(FOCUSABLE))
      if (items.length === 0) return
      const first = items[0]
      const last = items[items.length - 1]
      const active = document.activeElement
      // Focus sitting on a non-focusable node (clicked the title/body) lands on <body>; pull it
      // back into the panel before the browser's default Tab escapes behind the portal.
      if (!panelRef.current.contains(active)) {
        e.preventDefault()
        first.focus()
        return
      }
      if (e.shiftKey && active === first) {
        e.preventDefault()
        last.focus()
      } else if (!e.shiftKey && active === last) {
        e.preventDefault()
        first.focus()
      }
    },
    [onClose],
  )

  useEffect(() => {
    if (!open) return
    restoreFocusRef.current = document.activeElement as HTMLElement | null
    const { overflow } = document.body.style
    document.body.style.overflow = 'hidden'
    // Focus the first focusable node in the panel, skipping the close button itself — it's always
    // the very first focusable element in DOM order (rendered before any content), so without this
    // exclusion every modal would open with focus (and its visible ring) on "dismiss" rather than
    // on its actual first control. Falls back to the panel itself if there's truly nothing else.
    const raf = requestAnimationFrame(() => {
      const target =
        panelRef.current?.querySelector<HTMLElement>(FOCUSABLE_EXCEPT_CLOSE) ?? panelRef.current
      target?.focus()
    })
    return () => {
      cancelAnimationFrame(raf)
      document.body.style.overflow = overflow
      restoreFocusRef.current?.focus?.()
    }
  }, [open])

  if (!open) return null

  return createPortal(
    <div
      className="fixed inset-0 z-50 flex items-center justify-center p-4"
      onKeyDown={handleKeyDown}
    >
      <button
        type="button"
        aria-label="Close"
        aria-hidden="true"
        tabIndex={-1}
        onClick={onClose}
        className="absolute inset-0 bg-text-primary/30 backdrop-blur-[2px]"
      />
      <div
        ref={panelRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        tabIndex={-1}
        className={cn(
          'relative z-10 w-full rounded-2xl bg-surface p-5 shadow-card-lg outline-none',
          size === 'sm' ? 'max-w-sm' : 'max-w-lg',
        )}
      >
        <div className={cn('flex items-start gap-4', hideTitle ? 'justify-end' : 'mb-4 justify-between')}>
          <h2 id={titleId} className={hideTitle ? 'sr-only' : 'text-lg font-bold text-text-primary'}>
            {title}
          </h2>
          <button
            type="button"
            onClick={onClose}
            aria-label="Close"
            data-modal-close
            className="-m-1.5 rounded-lg p-1.5 text-text-secondary hover:bg-surface-subtle"
          >
            <X className="size-5" aria-hidden="true" />
          </button>
        </div>
        <div className="text-sm text-text-secondary">{children}</div>
        {footer && <div className="mt-6 flex justify-end gap-2">{footer}</div>}
      </div>
    </div>,
    document.body,
  )
}
