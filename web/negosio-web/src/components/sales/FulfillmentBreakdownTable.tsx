import type { SaleItemFulfillmentDto } from '../../api/types'
import { formatQty } from '../../lib/format'
import { fulfillmentStatusLabel } from '../../lib/pos'

interface RowDef {
  key: string
  quantity: (item: SaleItemFulfillmentDto) => number
  label: string
  /** Row 1 ("Sold") always renders, even at zero — every other row is omitted when its quantity
   * is 0 so a fully-taken-now line doesn't show six zeroes. */
  always?: boolean
}

// Rows 2-8 are sourced from the same `fulfillmentStatusLabel(method, status)` table that drives the
// schedule badges and cancellation history elsewhere on this page — one label table, one place to
// change it. Row 1 ("Sold") is the one exception: it's the original sale quantity, not a fulfillment
// status, so it has no entry in that table and stays a literal string.
const ROWS: RowDef[] = [
  { key: 'sold', quantity: (i) => i.quantity, label: 'Sold', always: true },
  { key: 'takeNow', quantity: (i) => i.takeNowQuantity, label: fulfillmentStatusLabel('TakeNow', 'Completed') },
  {
    key: 'deliveryUnscheduled',
    quantity: (i) => i.deliveryUnscheduledQuantity,
    label: fulfillmentStatusLabel('Delivery', 'Unscheduled'),
  },
  {
    key: 'deliveryPending',
    quantity: (i) => i.deliveryPendingQuantity,
    label: fulfillmentStatusLabel('Delivery', 'Pending'),
  },
  { key: 'delivered', quantity: (i) => i.deliveredQuantity, label: fulfillmentStatusLabel('Delivery', 'Completed') },
  {
    key: 'pickupUnscheduled',
    quantity: (i) => i.pickupUnscheduledQuantity,
    label: fulfillmentStatusLabel('Pickup', 'Unscheduled'),
  },
  {
    key: 'pickupPending',
    quantity: (i) => i.pickupPendingQuantity,
    label: fulfillmentStatusLabel('Pickup', 'Pending'),
  },
  { key: 'claimed', quantity: (i) => i.claimedQuantity, label: fulfillmentStatusLabel('Pickup', 'Completed') },
]

/**
 * The 8-row-per-line fulfillment breakdown. Every number is read straight off
 * `SaleItemFulfillmentDto` — nothing here is computed, since the server is the only source of
 * truth for how a sale's quantities split across take-now/delivery/pickup.
 */
export function FulfillmentBreakdownTable({ items }: { items: SaleItemFulfillmentDto[] }) {
  return (
    <div className="overflow-x-auto rounded-xl border border-border bg-surface">
      <table className="w-full text-left text-[13px]">
        <thead className="border-b border-border text-text-muted">
          <tr>
            <th className="px-3 py-2 font-medium">Item</th>
            <th className="px-3 py-2 font-medium">Status</th>
            <th className="px-3 py-2 text-right font-medium">Qty</th>
          </tr>
        </thead>
        <tbody>
          {items.map((item) => {
            const rows = ROWS.filter((r) => r.always || r.quantity(item) > 0)
            return rows.map((r, idx) => (
              <tr key={`${item.saleItemId}-${r.key}`} className="border-b border-border-light last:border-0">
                {idx === 0 && (
                  <td className="px-3 py-2 align-top text-text-primary" rowSpan={rows.length}>
                    {item.productName}
                    {item.variantName && <span className="text-text-muted"> · {item.variantName}</span>}
                  </td>
                )}
                <td className="px-3 py-2 text-text-secondary">{r.label}</td>
                <td className="px-3 py-2 text-right">{formatQty(r.quantity(item))}</td>
              </tr>
            ))
          })}
        </tbody>
      </table>
    </div>
  )
}
