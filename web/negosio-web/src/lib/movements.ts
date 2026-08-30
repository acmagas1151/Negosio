import type { StockMovementType } from '../api/types'

export const MOVEMENT_TYPE_LABELS: Record<StockMovementType, string> = {
  OpeningStock: 'Opening stock',
  AdjustmentIncrease: 'Adjustment in',
  AdjustmentDecrease: 'Adjustment out',
  Sale: 'Sale',
  Return: 'Return',
  TransferIn: 'Transfer in',
  TransferOut: 'Transfer out',
  Purchase: 'Purchase',
  Waste: 'Waste',
}

/** Movement types that raise stock on hand (the rest lower it). */
export const STOCK_INCREASE_TYPES: ReadonlySet<StockMovementType> = new Set([
  'OpeningStock',
  'AdjustmentIncrease',
  'Return',
  'TransferIn',
  'Purchase',
])

export function movementTypeLabel(type: StockMovementType): string {
  return MOVEMENT_TYPE_LABELS[type] ?? type
}
