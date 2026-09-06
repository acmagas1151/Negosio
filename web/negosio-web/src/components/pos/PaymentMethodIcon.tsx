import { Banknote, CreditCard, Landmark } from 'lucide-react'
import type { PaymentMethod } from '../../api/types'
import gcashLogo from '../../assets/payment-icons/gcash.svg'
import mayaLogo from '../../assets/payment-icons/maya.svg'

/**
 * GCash and Maya are wordmark logos (wide, not square), so they're sized by height only — forcing
 * them into the same square box as the lucide icons would squash or letterbox them. `h-6 w-auto`
 * matches the 24px lucide icons' height so everything still lines up on the same row.
 */
export function PaymentMethodIcon({ method, className }: { method: PaymentMethod; className?: string }) {
  switch (method) {
    case 'Cash':
      return <Banknote className={className} aria-hidden="true" />
    case 'Card':
      return <CreditCard className={className} aria-hidden="true" />
    case 'GCash':
      return <img src={gcashLogo} alt="" className="h-6 w-auto" aria-hidden="true" />
    case 'Maya':
      return <img src={mayaLogo} alt="" className="h-6 w-auto" aria-hidden="true" />
    case 'BankTransfer':
      return <Landmark className={className} aria-hidden="true" />
    case 'Other':
      return <Banknote className={className} aria-hidden="true" />
  }
}
