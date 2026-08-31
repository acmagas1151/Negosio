import type { SaleStatus } from '../../api/types'
import { SALE_STATUS_LABELS, saleStatusTone } from '../../lib/pos'
import { Badge } from '../ui'

export function StatusBadge({ status }: { status: SaleStatus }) {
  return <Badge tone={saleStatusTone(status)}>{SALE_STATUS_LABELS[status]}</Badge>
}
