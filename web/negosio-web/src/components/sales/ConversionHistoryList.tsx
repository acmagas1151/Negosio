import { Link } from 'react-router-dom'
import type { FulfillmentConversionDto } from '../../api/types'
import { formatQty } from '../../lib/format'
import { FULFILLMENT_METHOD_LABELS } from '../../lib/pos'

/**
 * The sale-level conversion log (a Delivery cancelled and replaced with a Pickup, or vice versa).
 * Most sales never have one of these, so an empty list renders nothing at all — no empty-state
 * card for a section that's absent far more often than present.
 */
export function ConversionHistoryList({ conversions }: { conversions: FulfillmentConversionDto[] }) {
  if (conversions.length === 0) return null

  const sorted = [...conversions].sort(
    (a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime(),
  )

  return (
    <div className="space-y-2">
      <h3 className="text-sm font-semibold text-text-primary">Conversion history</h3>
      <ul className="space-y-1.5">
        {sorted.map((c) => (
          <li key={c.id} className="rounded-xl border border-border bg-surface p-3 text-[13px] text-text-secondary">
            <span className="font-medium text-text-primary">
              {formatQty(c.quantity)} × {c.productName}
              {c.variantName ? ` · ${c.variantName}` : ''}
            </span>
            {' — '}
            {c.sourceRecordId ? (
              <Link
                to={`/delivery-receipts/${c.sourceRecordId}`}
                target="_blank"
                rel="noopener"
                className="font-medium text-primary-600 hover:underline"
              >
                {FULFILLMENT_METHOD_LABELS[c.fromMethod]}
              </Link>
            ) : (
              FULFILLMENT_METHOD_LABELS[c.fromMethod]
            )}
            {' → '}
            {c.replacementRecordId ? (
              <Link
                to={`/delivery-receipts/${c.replacementRecordId}`}
                target="_blank"
                rel="noopener"
                className="font-medium text-primary-600 hover:underline"
              >
                {FULFILLMENT_METHOD_LABELS[c.toMethod]}
              </Link>
            ) : (
              FULFILLMENT_METHOD_LABELS[c.toMethod]
            )}
            {' · “'}
            {c.reason}
            {'” · '}
            {c.createdByName}
            {' · '}
            {new Date(c.createdAtUtc).toLocaleString()}
          </li>
        ))}
      </ul>
    </div>
  )
}
