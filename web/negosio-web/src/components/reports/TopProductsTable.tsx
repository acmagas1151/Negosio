import { Package } from 'lucide-react'
import type { TopProductDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'
import { Button, EmptyState, ErrorState, SkeletonText, Table } from '../ui'

interface Props {
  products: TopProductDto[] | undefined
  top: 10 | 20
  onTopChange: (top: 10 | 20) => void
  isPending: boolean
  isError: boolean
  onRetry: () => void
}

export function TopProductsTable({ products, top, onTopChange, isPending, isError, onRetry }: Props) {
  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold text-text-primary">Top products</h2>
        <div className="flex gap-1">
          {([10, 20] as const).map((n) => (
            <Button
              key={n}
              size="sm"
              variant={top === n ? 'primary' : 'ghost'}
              onClick={() => onTopChange(n)}
            >
              Top {n}
            </Button>
          ))}
        </div>
      </div>

      {isError ? (
        <ErrorState message="Could not load top products." onRetry={onRetry} />
      ) : isPending ? (
        <div className="space-y-2">
          {Array.from({ length: 5 }).map((_, i) => (
            <SkeletonText key={i} className="h-8" />
          ))}
        </div>
      ) : !products || products.length === 0 ? (
        <EmptyState icon={Package} title="No products sold" description="Try a wider date range or different filters." />
      ) : (
        <Table>
          <Table.Head>
            <Table.HeaderCell>Product</Table.HeaderCell>
            <Table.HeaderCell>SKU</Table.HeaderCell>
            <Table.HeaderCell align="right">Qty sold</Table.HeaderCell>
            <Table.HeaderCell align="right">Sales amount</Table.HeaderCell>
          </Table.Head>
          <Table.Body>
            {products.map((p) => (
              <Table.Row key={p.productVariantId}>
                <Table.Cell className="font-medium text-text-primary">
                  {p.productName}
                  {p.variantName && <span className="text-text-muted"> · {p.variantName}</span>}
                  {p.categoryName && (
                    <span className="ml-1.5 text-[12px] text-text-muted">({p.categoryName})</span>
                  )}
                </Table.Cell>
                <Table.Cell className="text-text-secondary">{p.sku ?? '—'}</Table.Cell>
                <Table.Cell align="right">{formatQty(p.quantitySold)}</Table.Cell>
                <Table.Cell align="right" className="font-medium text-text-primary">
                  {formatMoney(p.salesAmount)}
                </Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table>
      )}
    </div>
  )
}
