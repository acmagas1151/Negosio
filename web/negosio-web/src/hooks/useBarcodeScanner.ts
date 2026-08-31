import { useEffect, useRef } from 'react'

const RESET_MS = 50
const MIN_LEN = 3

/** Whether keystrokes on this element are the user typing into a field, not a wedge scan. */
function isEditableTarget(el: EventTarget | null): boolean {
  if (!(el instanceof HTMLElement)) return false
  const tag = el.tagName
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || el.isContentEditable
}

/**
 * USB barcode scanners act as keyboard wedges: a fast burst of keystrokes ending in Enter.
 * We buffer printable characters and, on Enter, fire `onScan` if the burst was fast and long
 * enough.
 *
 * The global listener stays out of the way whenever a form field is focused (`input`,
 * `textarea`, `select`, contenteditable) — including the always-focused POS search box, whose
 * own Enter handler does the barcode-first lookup. When focus is anywhere else (the product
 * grid, empty space) this hook catches the scan.
 */
export function useBarcodeScanner({
  enabled,
  onScan,
}: {
  enabled: boolean
  onScan: (code: string) => void
}) {
  const buf = useRef('')
  const last = useRef(0)
  const onScanRef = useRef(onScan)

  useEffect(() => {
    onScanRef.current = onScan
  }, [onScan])

  useEffect(() => {
    if (!enabled) return

    function handler(e: KeyboardEvent) {
      // A focused field owns its own keystrokes (search box, reason textarea, qty input…).
      if (isEditableTarget(e.target)) return

      const now = Date.now()
      if (now - last.current > RESET_MS) buf.current = ''
      last.current = now

      if (e.key === 'Enter') {
        const code = buf.current
        buf.current = ''
        if (code.length >= MIN_LEN) {
          e.preventDefault()
          onScanRef.current(code)
        }
        return
      }
      if (e.key.length === 1) buf.current += e.key
    }

    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [enabled])
}
