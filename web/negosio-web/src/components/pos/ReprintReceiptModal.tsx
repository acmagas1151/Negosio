import type { SaleDetailDto } from '../../api/types'
import { TransactionLookupModal } from './TransactionLookupModal'

interface Props {
  open: boolean
  onClose: () => void
  /** Prefills the search with the terminal's most recent sale — a suggestion, not an automatic pick. */
  suggestedSaleNumber?: string
}

/**
 * Reprint is a read-only lookup — it never mutates a Sale. Reuses the exact receipt
 * route/template every other "print receipt" entry point uses (SaleDetailPage, PosCompletePage);
 * no second receipt template. Branch scoping for who can find which sale is enforced by the same
 * backend call TransactionLookupModal always uses.
 */
export function ReprintReceiptModal({ open, onClose, suggestedSaleNumber }: Props) {
  return (
    <TransactionLookupModal
      open={open}
      onClose={onClose}
      title="Reprint receipt"
      actionLabel="Print receipt"
      isEligible={() => ({ ok: true })}
      initialSaleNumber={suggestedSaleNumber}
      onContinue={(sale: SaleDetailDto) => {
        window.open(`/sales/${sale.sale.id}/receipt?print=1`, '_blank', 'noopener')
        onClose()
      }}
    />
  )
}
