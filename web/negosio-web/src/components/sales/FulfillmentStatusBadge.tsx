import type { FulfillmentMethod, FulfillmentStatus } from '../../api/types'
import { fulfillmentStatusLabel, fulfillmentStatusTone } from '../../lib/pos'
import { Badge } from '../ui'

export function FulfillmentStatusBadge({
  method,
  status,
}: {
  method: FulfillmentMethod
  status: FulfillmentStatus
}) {
  return <Badge tone={fulfillmentStatusTone(status)}>{fulfillmentStatusLabel(method, status)}</Badge>
}
