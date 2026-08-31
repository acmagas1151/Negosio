import type { StockMovementType } from '../../api/types'
import { MOVEMENT_TYPE_LABELS, STOCK_INCREASE_TYPES } from '../../lib/movements'
import { Badge } from '../ui'

export function MovementTypeBadge({ type }: { type: StockMovementType }) {
  return (
    <Badge tone={STOCK_INCREASE_TYPES.has(type) ? 'neutral' : 'warning'}>
      {MOVEMENT_TYPE_LABELS[type] ?? type}
    </Badge>
  )
}
