import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Search } from 'lucide-react'
import { posCatalogApi } from '../../api/pos'
import type { PosCatalogItemDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'
import { useDebouncedValue } from '../../hooks/useDebouncedValue'
import { cn } from '../../lib/cn'
import { Button, EmptyState, Modal, SkeletonText, useToast } from '../ui'
import { inputClass } from '../ui/TextField'

interface Props {
  open: boolean
  onClose: () => void
  branchId: string
  onAddToCart: (item: PosCatalogItemDto) => void
}

/**
 * Read-only price/stock lookup — never creates a Sale, reserves inventory, or mutates stock.
 * Reuses the same POS catalog search the product grid uses, which already omits cost price from
 * PosCatalogItemDto entirely, so there is nothing to redact here.
 */
export function CheckPriceModal({ open, onClose, branchId, onAddToCart }: Props) {
  const { toast } = useToast()
  const [term, setTerm] = useState('')
  const debounced = useDebouncedValue(term, 250)

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setTerm('')
  }, [open])

  const query = useQuery({
    queryKey: ['pos-catalog', 'check-price', { branchId, search: debounced }],
    queryFn: () => posCatalogApi.search({ branchId, search: debounced || undefined, page: 1, pageSize: 10 }),
    enabled: open,
  })

  return (
    <Modal open={open} onClose={onClose} title="Check price">
      <div className="relative">
        <Search
          className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-text-muted"
          aria-hidden="true"
        />
        <input
          type="text"
          value={term}
          onChange={(e) => setTerm(e.target.value)}
          placeholder="Search by name, SKU, or barcode — or scan"
          aria-label="Search products"
          autoFocus
          className={cn(inputClass, 'pl-9')}
        />
      </div>

      <div className="mt-3 max-h-80 space-y-2 overflow-y-auto">
        {query.isPending && debounced ? (
          Array.from({ length: 3 }).map((_, i) => <SkeletonText key={i} className="h-16 rounded-xl" />)
        ) : !query.data || query.data.items.length === 0 ? (
          <EmptyState
            icon={Search}
            title={debounced ? 'No products match' : 'Start typing to search'}
            description={debounced ? 'Try a different name, SKU, or barcode.' : undefined}
          />
        ) : (
          query.data.items.map((item) => (
            <div
              key={item.productVariantId}
              className="flex items-center justify-between gap-3 rounded-xl border border-border p-3"
            >
              <div className="min-w-0">
                <p className="truncate text-sm font-semibold text-text-primary">
                  {item.productName}
                  {item.variantName ? ` — ${item.variantName}` : ''}
                </p>
                <p className="truncate text-[12px] text-text-muted">
                  {item.sku ? `SKU: ${item.sku}` : ''}
                  {item.sku && item.barcode ? ' · ' : ''}
                  {item.barcode ? `Barcode: ${item.barcode}` : ''}
                </p>
                <p className="text-[13px] text-text-secondary">
                  {formatMoney(item.sellingPrice)}
                  {item.trackInventory && (
                    <span className="text-text-muted">
                      {' '}
                      · {item.isAvailable ? `${formatQty(item.quantityAvailable)} in stock` : 'Out of stock'}
                    </span>
                  )}
                </p>
              </div>
              <Button
                variant="secondary"
                size="sm"
                disabled={!item.isAvailable}
                onClick={() => {
                  onAddToCart(item)
                  toast('success', `Added ${item.productName}`)
                  onClose()
                }}
              >
                Add to cart
              </Button>
            </div>
          ))
        )}
      </div>
    </Modal>
  )
}
