import type { DeliveryStatus } from '../../api/types'
import { DELIVERY_STATUS_LABELS, deliveryStatusTone } from '../../lib/pos'
import { Badge } from '../ui'

export function DeliveryStatusBadge({ status }: { status: DeliveryStatus }) {
  return <Badge tone={deliveryStatusTone(status)}>{DELIVERY_STATUS_LABELS[status]}</Badge>
}
