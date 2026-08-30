import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { inventoryApi } from '../../api/inventory'
import { ApiError } from '../../api/client'
import type { AdjustInventoryRequest, InventoryRowDto } from '../../api/types'
import { formatQty } from '../../lib/format'
import { cn } from '../../lib/cn'
import { Button, Callout, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  /** The inventory row being adjusted. `null` while the modal is closed. */
  row: InventoryRowDto | null
}

type Direction = 'add' | 'remove'

export function AdjustStockModal({ open, onClose, row }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  // A local copy so a 409 can refresh the displayed on-hand + concurrency token in place,
  // without the list page having to re-open the modal.
  const [current, setCurrent] = useState<InventoryRowDto | null>(row)
  const [direction, setDirection] = useState<Direction>('add')
  const [quantity, setQuantity] = useState('')
  const [reason, setReason] = useState('')
  const [reorderLevel, setReorderLevel] = useState('')
  const [quantityError, setQuantityError] = useState('')
  const [reasonError, setReasonError] = useState('')
  const [conflict, setConflict] = useState(false)

  useEffect(() => {
    if (!open || !row) return
    // oxlint-disable-next-line set-state-in-effect
    setCurrent(row)
    setDirection('add')
    setQuantity('')
    setReason('')
    setReorderLevel(String(row.reorderLevel))
    setQuantityError('')
    setReasonError('')
    setConflict(false)
  }, [open, row])

  const mutation = useMutation({
    mutationFn: () => {
      const active = current!
      const qty = Number(quantity)
      const body: AdjustInventoryRequest = {
        branchId: active.branchId,
        productId: active.productId,
        productVariantId: active.productVariantId,
        adjustment: direction === 'add' ? qty : -qty,
        reason: reason.trim(),
        reorderLevel: reorderLevel.trim() === '' ? null : Number(reorderLevel),
        expectedConcurrencyToken: active.concurrencyToken,
      }
      return inventoryApi.adjust(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['inventory'] })
      toast('success', 'Stock adjusted')
      onClose()
    },
    onError: async (error) => {
      if (error instanceof ApiError && error.status === 409 && error.code === 'INVENTORY_CONCURRENCY_CONFLICT') {
        setConflict(true)
        qc.invalidateQueries({ queryKey: ['inventory'] })
        if (row) {
          try {
            setCurrent(await inventoryApi.get(row.id))
          } catch {
            // keep the stale copy; the callout still tells the user to review
          }
        }
        return
      }
      if (error instanceof ApiError && error.code === 'INSUFFICIENT_INVENTORY') {
        setQuantityError(
          `That would take stock below zero (current: ${formatQty(current?.quantityOnHand ?? 0)}).`,
        )
        return
      }
      toast('error', error instanceof Error ? error.message : 'Something went wrong.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setQuantityError('')
    setReasonError('')

    const qty = Number(quantity)
    if (!(qty > 0)) {
      setQuantityError('Enter a quantity greater than zero.')
      return
    }
    if (!reason.trim()) {
      setReasonError('A reason is required.')
      return
    }
    if (direction === 'remove' && current && qty > current.quantityOnHand) {
      setQuantityError(
        `Only ${formatQty(current.quantityOnHand)} on hand — you can't remove more than that.`,
      )
      return
    }
    mutation.mutate()
  }

  if (!row || !current) return null

  const variantLabel = current.isDefaultVariant ? null : current.variantName

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Adjust stock"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            Apply adjustment
          </Button>
        </>
      }
    >
      {conflict && (
        <Callout tone="warning">
          Someone else changed this item&rsquo;s stock while you were editing. The current quantity
          below has been refreshed — review it and submit again.
        </Callout>
      )}

      <dl className="mb-4 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 rounded-lg bg-surface-subtle px-3 py-2.5 text-[13px]">
        <dt className="text-text-muted">Product</dt>
        <dd className="font-medium text-text-primary">
          {current.productName}
          {variantLabel && <span className="text-text-muted"> · {variantLabel}</span>}
        </dd>
        <dt className="text-text-muted">Branch</dt>
        <dd className="text-text-secondary">{current.branchName}</dd>
        <dt className="text-text-muted">Current on hand</dt>
        <dd className="font-semibold text-text-primary">{formatQty(current.quantityOnHand)}</dd>
      </dl>

      <form onSubmit={submit} className="space-y-1">
        <div className="mb-3 flex flex-col gap-1.5">
          <span className="text-sm font-semibold text-text-secondary">Direction</span>
          <div className="inline-flex rounded-lg border border-border-strong p-0.5">
            {(['add', 'remove'] as const).map((d) => (
              <button
                key={d}
                type="button"
                aria-pressed={direction === d}
                onClick={() => setDirection(d)}
                className={cn(
                  'flex-1 rounded-md px-3 py-1.5 text-sm font-medium transition-colors',
                  direction === d
                    ? 'bg-primary-600 text-white'
                    : 'text-text-secondary hover:bg-surface-subtle',
                )}
              >
                {d === 'add' ? 'Add stock' : 'Remove stock'}
              </button>
            ))}
          </div>
        </div>

        <TextField
          label="Quantity"
          name="quantity"
          type="number"
          min={0}
          step="0.001"
          value={quantity}
          onChange={(e) => setQuantity(e.target.value)}
          error={quantityError || undefined}
          autoFocus
        />

        <div className="flex flex-col gap-1.5">
          <label className="text-sm font-semibold text-text-secondary" htmlFor="adjust-reason">
            Reason
          </label>
          <textarea
            id="adjust-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={2}
            maxLength={500}
            placeholder="e.g. Stock count correction, damaged goods, received delivery"
            className="w-full rounded-lg border border-border-strong bg-white px-3 py-2 text-sm text-text-primary placeholder:text-text-muted focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
          <p className={cn('min-h-[1rem] text-[13px] leading-4', reasonError ? 'text-danger' : 'text-text-muted')}>
            {reasonError || ' '}
          </p>
        </div>

        <TextField
          label="Reorder level"
          name="reorderLevel"
          type="number"
          min={0}
          step="0.001"
          value={reorderLevel}
          onChange={(e) => setReorderLevel(e.target.value)}
          hint="Stock at or below this level is flagged as low."
        />
      </form>
    </Modal>
  )
}
