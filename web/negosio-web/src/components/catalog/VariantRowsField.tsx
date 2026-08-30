import { Plus, Trash2 } from 'lucide-react'
import { Button, TextField } from '../ui'

export interface VariantRow {
  name: string
  sku: string
  barcode: string
  costPrice: string
  sellingPrice: string
}

const EMPTY_ROW: VariantRow = { name: '', sku: '', barcode: '', costPrice: '0', sellingPrice: '0' }

interface Props {
  rows: VariantRow[]
  onChange: (rows: VariantRow[]) => void
}

export function VariantRowsField({ rows, onChange }: Props): React.ReactNode {
  const update = (index: number, patch: Partial<VariantRow>) =>
    onChange(rows.map((row, i) => (i === index ? { ...row, ...patch } : row)))

  const remove = (index: number) => onChange(rows.filter((_, i) => i !== index))

  return (
    <div className="space-y-3">
      {rows.map((row, i) => (
        <div key={i} className="rounded-lg border border-border-strong p-3">
          <div className="flex items-center justify-between">
            <p className="text-[13px] font-semibold text-text-secondary">Variant {i + 1}</p>
            <Button
              variant="ghost"
              size="sm"
              disabled={rows.length <= 1}
              onClick={() => remove(i)}
              leadingIcon={<Trash2 className="size-4" aria-hidden="true" />}
            >
              Remove
            </Button>
          </div>
          <TextField
            label="Name *"
            name={`variant-${i}-name`}
            value={row.name}
            maxLength={80}
            onChange={(e) => update(i, { name: e.target.value })}
          />
          <div className="grid grid-cols-1 gap-x-3 sm:grid-cols-2">
            <TextField
              label="SKU"
              name={`variant-${i}-sku`}
              value={row.sku}
              maxLength={64}
              onChange={(e) => update(i, { sku: e.target.value })}
            />
            <TextField
              label="Barcode"
              name={`variant-${i}-barcode`}
              value={row.barcode}
              maxLength={64}
              onChange={(e) => update(i, { barcode: e.target.value })}
            />
            <TextField
              label="Cost price"
              name={`variant-${i}-cost`}
              type="number"
              min={0}
              step="0.01"
              value={row.costPrice}
              onChange={(e) => update(i, { costPrice: e.target.value })}
            />
            <TextField
              label="Selling price"
              name={`variant-${i}-selling`}
              type="number"
              min={0}
              step="0.01"
              value={row.sellingPrice}
              onChange={(e) => update(i, { sellingPrice: e.target.value })}
            />
          </div>
        </div>
      ))}
      <Button
        variant="secondary"
        size="sm"
        onClick={() => onChange([...rows, { ...EMPTY_ROW }])}
        leadingIcon={<Plus className="size-4" aria-hidden="true" />}
      >
        Add variant
      </Button>
    </div>
  )
}
