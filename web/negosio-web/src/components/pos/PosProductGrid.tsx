import type { PosCatalogItemDto } from '../../api/types'
import { ProductCard } from './ProductCard'

interface Props {
  items: PosCatalogItemDto[]
  onAdd: (item: PosCatalogItemDto) => void
}

export function PosProductGrid({ items, onAdd }: Props) {
  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5">
      {items.map((item) => (
        <ProductCard key={item.productVariantId} item={item} onAdd={onAdd} />
      ))}
    </div>
  )
}
