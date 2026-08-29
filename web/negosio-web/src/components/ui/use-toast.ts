import { createContext, useContext } from 'react'

export type ToastTone = 'success' | 'warning' | 'error' | 'info'

export interface ToastItem {
  id: number
  tone: ToastTone
  message: string
}

export interface ToastContextValue {
  /** Push a toast. Returns its id. */
  toast: (tone: ToastTone, message: string) => number
  dismiss: (id: number) => void
}

export const ToastContext = createContext<ToastContextValue | undefined>(undefined)

export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error('useToast must be used within a ToastProvider')
  return ctx
}
