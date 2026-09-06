import { Package, Plus } from 'lucide-react'
import type { PosCatalogItemDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'
import { cn } from '../../lib/cn'

interface Props {
  item: PosCatalogItemDto
  onAdd: (item: PosCatalogItemDto) => void
}

/** No product-image field exists in the catalog data model — this is an intentional placeholder
 * treatment (icon on a soft tint), never a broken-image state or an invented external URL. */
function Thumbnail() {
  return (
    <div className="grid aspect-square w-full shrink-0 place-items-center rounded-lg bg-primary-50">
      <Package className="size-7 text-primary-300" aria-hidden="true" />
    </div>
  )
}

export function ProductCard({ item, onAdd }: Props) {
  return (
    <div
      className={cn(
        'flex flex-col gap-2 rounded-xl border border-border bg-surface p-2.5 shadow-card transition-all',
        item.isAvailable ? 'hover:-translate-y-0.5 hover:border-primary-200 hover:shadow-card-lg' : 'opacity-55',
      )}
    >
      <Thumbnail />

      <div className="min-w-0 flex-1">
        <p className="line-clamp-2 text-[13px] font-semibold leading-snug text-text-primary">
          {item.productName}
        </p>
        {item.variantName && (
          <p className="truncate text-[11px] text-text-muted">{item.variantName}</p>
        )}
      </div>

      <div className="flex items-center justify-between">
        <span className="text-sm font-bold text-text-primary">{formatMoney(item.sellingPrice)}</span>
        {item.trackInventory && (
          <span
            className={cn(
              'text-[11px] font-medium',
              item.isAvailable ? 'text-success-strong' : 'text-danger-strong',
            )}
          >
            {item.isAvailable ? `${formatQty(item.quantityAvailable)} in stock` : 'Out of stock'}
          </span>
        )}
      </div>

      <button
        type="button"
        disabled={!item.isAvailable}
        onClick={() => onAdd(item)}
        className={cn(
          'flex h-8 items-center justify-center gap-1 rounded-lg text-[13px] font-semibold transition-colors',
          item.isAvailable
            ? 'bg-primary-50 text-primary-700 hover:bg-primary-100'
            : 'cursor-not-allowed bg-surface-subtle text-text-muted',
        )}
      >
        <Plus className="size-3.5" aria-hidden="true" />
        Add
      </button>
    </div>
  )
}
