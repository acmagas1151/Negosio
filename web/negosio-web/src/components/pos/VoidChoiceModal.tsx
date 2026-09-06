import { Ban, Search } from 'lucide-react'
import { Modal } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  cartItemCount: number
  onCancelTransaction: () => void
  onVoidExistingSale: () => void
}

function ChoiceButton({
  icon: Icon,
  label,
  description,
  onClick,
}: {
  icon: typeof Ban
  label: string
  description: string
  onClick: () => void
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="flex w-full items-start gap-3 rounded-xl border border-border p-3 text-left hover:border-border-strong hover:bg-surface-subtle"
    >
      <Icon className="mt-0.5 size-5 shrink-0 text-text-secondary" aria-hidden="true" />
      <span>
        <span className="block text-sm font-semibold text-text-primary">{label}</span>
        <span className="block text-[13px] text-text-muted">{description}</span>
      </span>
    </button>
  )
}

/** The Void button's entry point — a cart being worked on isn't a Sale yet, so cancelling it is a
 * different action (and outcome) from voiding one that's already been checked out. */
export function VoidChoiceModal({
  open,
  onClose,
  cartItemCount,
  onCancelTransaction,
  onVoidExistingSale,
}: Props) {
  return (
    <Modal open={open} onClose={onClose} title="Void">
      <div className="space-y-2">
        <ChoiceButton
          icon={Ban}
          label="Cancel current transaction"
          description={`Clear the ${cartItemCount} item${cartItemCount === 1 ? '' : 's'} in the cart. Nothing is recorded — no sale number is used.`}
          onClick={onCancelTransaction}
        />
        <ChoiceButton
          icon={Search}
          label="Void an existing sale"
          description="Search by sale number to void a completed sale."
          onClick={onVoidExistingSale}
        />
      </div>
    </Modal>
  )
}
