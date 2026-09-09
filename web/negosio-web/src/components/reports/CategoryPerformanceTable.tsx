import { Tag } from 'lucide-react'
import type { CategoryPerformanceDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'
import { EmptyState, ErrorState, SkeletonText, Table } from '../ui'

interface Props {
  categories: CategoryPerformanceDto[] | undefined
  isPending: boolean
  isError: boolean
  onRetry: () => void
}

export function CategoryPerformanceTable({ categories, isPending, isError, onRetry }: Props) {
  return (
    <div className="space-y-3">
      <h2 className="text-sm font-semibold text-text-primary">Category performance</h2>

      {isError ? (
        <ErrorState message="Could not load category performance." onRetry={onRetry} />
      ) : isPending ? (
        <div className="space-y-2">
          {Array.from({ length: 4 }).map((_, i) => (
            <SkeletonText key={i} className="h-8" />
          ))}
        </div>
      ) : !categories || categories.length === 0 ? (
        <EmptyState icon={Tag} title="No category sales" description="Try a wider date range or different filters." />
      ) : (
        <Table>
          <Table.Head>
            <Table.HeaderCell>Category</Table.HeaderCell>
            <Table.HeaderCell align="right">Qty sold</Table.HeaderCell>
            <Table.HeaderCell align="right">Sales amount</Table.HeaderCell>
            <Table.HeaderCell align="right">% of sales</Table.HeaderCell>
          </Table.Head>
          <Table.Body>
            {categories.map((c) => (
              <Table.Row key={c.categoryId ?? 'uncategorized'}>
                <Table.Cell className="font-medium text-text-primary">{c.categoryName}</Table.Cell>
                <Table.Cell align="right">{formatQty(c.quantitySold)}</Table.Cell>
                <Table.Cell align="right" className="font-medium text-text-primary">
                  {formatMoney(c.salesAmount)}
                </Table.Cell>
                <Table.Cell align="right" className="text-text-secondary">
                  {c.percentageOfSales}%
                </Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table>
      )}
    </div>
  )
}
