import { Plus, Ban, Percent, Undo2, Printer, Tag } from 'lucide-react'
import { Button } from '../ui'

interface Props {
  onNewTransaction: () => void
  onVoid: () => void
  onDiscounts: () => void
  onReturns: () => void
  onReprint: () => void
  onCheckPrice: () => void
  canVoid: boolean
  canReturn: boolean
  discountsDisabled: boolean
}

const icon = (I: typeof Plus) => <I className="size-4" aria-hidden="true" />

/** The primary transaction-action toolbar, rendered under the session header. */
export function PosActionBar({
  onNewTransaction,
  onVoid,
  onDiscounts,
  onReturns,
  onReprint,
  onCheckPrice,
  canVoid,
  canReturn,
  discountsDisabled,
}: Props) {
  return (
    <div className="flex shrink-0 flex-wrap items-center gap-2 border-b border-border bg-surface px-4 py-2.5">
      <Button size="sm" leadingIcon={icon(Plus)} onClick={onNewTransaction} title="New transaction (F1)">
        New transaction
      </Button>
      {canVoid && (
        <Button variant="destructive" size="sm" leadingIcon={icon(Ban)} onClick={onVoid}>
          Void
        </Button>
      )}
      <Button
        variant="secondary"
        size="sm"
        leadingIcon={icon(Percent)}
        onClick={onDiscounts}
        disabled={discountsDisabled}
        title="Discounts (F3)"
      >
        Discounts
      </Button>
      {canReturn && (
        <Button
          variant="secondary"
          size="sm"
          leadingIcon={icon(Undo2)}
          onClick={onReturns}
          title="Returns (F4)"
        >
          Returns
        </Button>
      )}
      <Button
        variant="secondary"
        size="sm"
        leadingIcon={icon(Printer)}
        onClick={onReprint}
        title="Reprint receipt (F5)"
      >
        Reprint receipt
      </Button>
      <Button
        variant="secondary"
        size="sm"
        leadingIcon={icon(Tag)}
        onClick={onCheckPrice}
        title="Check price (F2)"
      >
        Check price
      </Button>
    </div>
  )
}
