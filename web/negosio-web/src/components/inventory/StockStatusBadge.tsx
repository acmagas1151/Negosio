import type { InventoryStatus } from '../../api/types'
import { Badge } from '../ui'

/**
 * Subtle stock signalling: "In stock" is plain text (no colour), only Low / Out get a badge.
 */
export function StockStatusBadge({ status }: { status: InventoryStatus }) {
  if (status === 'OutOfStock') return <Badge tone="danger">Out of stock</Badge>
  if (status === 'LowStock') return <Badge tone="warning">Low stock</Badge>
  return <span className="text-[13px] text-text-muted">In stock</span>
}
