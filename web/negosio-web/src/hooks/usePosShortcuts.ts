import { useEffect, useRef } from 'react'

/** Whether keystrokes on this element are the user typing into a field, not a shortcut. */
function isEditableTarget(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false
  const tag = el.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable
}

export interface PosShortcutHandlers {
  onNewTransaction: () => void
  onCheckPrice: () => void
  onDiscounts: () => void
  onReturns: () => void
  onReprint: () => void
}

/**
 * F1 New transaction · F2 Check price · F3 Discounts · F4 Returns · F5 Reprint receipt.
 * Ignored while a form field has focus (barcode-scanner-safe, mirrors useBarcodeScanner's guard)
 * and while any POS modal is open, so a shortcut can't fire behind an open dialog. No Void
 * shortcut — it's destructive and gets no fast key. F5 is browser-reserved for refresh; it is
 * preventDefault()'d here and was confirmed non-refreshing in real-browser verification.
 */
export function usePosShortcuts(handlers: PosShortcutHandlers, enabled: boolean) {
  const handlersRef = useRef(handlers)
  useEffect(() => {
    handlersRef.current = handlers
  }, [handlers])

  useEffect(() => {
    if (!enabled) return

    function onKeyDown(e: KeyboardEvent) {
      if (isEditableTarget(e.target)) return
      if (e.repeat) return

      switch (e.key) {
        case 'F1':
          e.preventDefault()
          handlersRef.current.onNewTransaction()
          break
        case 'F2':
          e.preventDefault()
          handlersRef.current.onCheckPrice()
          break
        case 'F3':
          e.preventDefault()
          handlersRef.current.onDiscounts()
          break
        case 'F4':
          e.preventDefault()
          handlersRef.current.onReturns()
          break
        case 'F5':
          e.preventDefault()
          handlersRef.current.onReprint()
          break
        default:
          break
      }
    }

    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [enabled])
}
