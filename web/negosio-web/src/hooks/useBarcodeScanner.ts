import { useEffect, useRef } from 'react'

const RESET_MS = 50
const MIN_LEN = 3

/**
 * USB barcode scanners act as keyboard wedges: a fast burst of keystrokes ending in Enter.
 * We buffer printable characters and, on Enter, fire `onScan` if the burst was fast and long
 * enough. Keystrokes into an input/textarea inside a modal are ignored (so typing a reason
 * doesn't register as a scan); the POS search box is not a modal field, so scanning into it works.
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
      const now = Date.now()
      if (now - last.current > RESET_MS) buf.current = ''
      last.current = now

      const target = e.target as HTMLElement | null
      const inModalField =
        !!target &&
        (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA') &&
        !!target.closest('[role="dialog"]')
      if (inModalField) return

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
