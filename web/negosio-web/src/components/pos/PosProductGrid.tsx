import type { PosCatalogItemDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'
import { cn } from '../../lib/cn'

interface Props {
  items: PosCatalogItemDto[]
  onAdd: (item: PosCatalogItemDto) => void
}

export function PosProductGrid({ items, onAdd }: Props) {
  return (
    <div className="grid grid-cols-2 gap-2 md:grid-cols-3 xl:grid-cols-4">
      {items.map((item) => (
        <button
          key={item.productVariantId}
          type="button"
          disabled={!item.isAvailable}
          onClick={() => onAdd(item)}
          className={cn(
            'flex h-28 flex-col justify-between rounded-xl border border-border bg-surface p-3 text-left transition-colors',
            item.isAvailable
              ? 'hover:border-primary-300 hover:bg-primary-50/40'
              : 'cursor-not-allowed opacity-55',
          )}
        >
          <div className="min-w-0">
            <p className="line-clamp-2 text-sm font-semibold text-text-primary">{item.productName}</p>
            {item.variantName && (
              <p className="truncate text-[12px] text-text-muted">{item.variantName}</p>
            )}
          </div>
          <div className="flex items-end justify-between">
            <span className="text-sm font-bold text-text-primary">
              {formatMoney(item.sellingPrice)}
            </span>
            {item.trackInventory && (
              <span className="text-[11px] text-text-muted">
                {item.isAvailable ? `${formatQty(item.quantityAvailable)} in stock` : 'Out of stock'}
              </span>
            )}
          </div>
        </button>
      ))}
    </div>
  )
}
