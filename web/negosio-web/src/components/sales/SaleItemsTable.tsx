import type { SaleItemDto } from '../../api/types'
import { formatMoney, formatQty } from '../../lib/format'
import { Table } from '../ui'

export function SaleItemsTable({ items }: { items: SaleItemDto[] }) {
  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Item</Table.HeaderCell>
        <Table.HeaderCell align="right">Qty</Table.HeaderCell>
        <Table.HeaderCell align="right">Unit</Table.HeaderCell>
        <Table.HeaderCell align="right">Gross</Table.HeaderCell>
        <Table.HeaderCell align="right">Discount</Table.HeaderCell>
        <Table.HeaderCell align="right">Tax</Table.HeaderCell>
        <Table.HeaderCell align="right">Net</Table.HeaderCell>
        <Table.HeaderCell align="right">Returned</Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {items.map((i) => (
          <Table.Row key={i.id}>
            <Table.Cell>
              <span className="font-medium text-text-primary">{i.productName}</span>
              {i.variantName && <span className="text-text-muted"> · {i.variantName}</span>}
              {i.sku && <span className="block text-[12px] text-text-muted">{i.sku}</span>}
            </Table.Cell>
            <Table.Cell align="right">{formatQty(i.quantity)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(i.unitPrice)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(i.grossAmount)}</Table.Cell>
            <Table.Cell align="right">
              {i.discountAmount > 0 ? `−${formatMoney(i.discountAmount)}` : '—'}
            </Table.Cell>
            <Table.Cell align="right">{formatMoney(i.taxAmount)}</Table.Cell>
            <Table.Cell align="right" className="font-semibold text-text-primary">
              {formatMoney(i.netAmount)}
            </Table.Cell>
            <Table.Cell align="right">
              {i.returnedQuantity > 0 ? formatQty(i.returnedQuantity) : '—'}
            </Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}
