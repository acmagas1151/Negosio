import type { CategoryDto } from '../../api/types'
import { cn } from '../../lib/cn'
import { Select, TextField } from '../ui'

/** The shared "Details" field group — used by the create form (Task 14) and the edit form (Task 15). */
export interface ProductDetailsValue {
  categoryId: string
  name: string
  description: string
  trackInventory: boolean
}

interface Props {
  value: ProductDetailsValue
  onChange: (value: ProductDetailsValue) => void
  categories: CategoryDto[]
  errors: Record<string, string>
}

export function ProductFields({ value, onChange, categories, errors }: Props): React.ReactNode {
  return (
    <div className="space-y-1">
      <Select
        label="Category"
        name="categoryId"
        value={value.categoryId}
        error={errors.categoryid || undefined}
        onChange={(e) => onChange({ ...value, categoryId: e.target.value })}
      >
        <option value="">Select a category</option>
        {categories.map((c) => (
          <option key={c.id} value={c.id}>
            {c.name}
          </option>
        ))}
      </Select>

      <TextField
        label="Name"
        name="name"
        value={value.name}
        maxLength={120}
        error={errors.name || undefined}
        onChange={(e) => onChange({ ...value, name: e.target.value })}
      />

      <div className="flex flex-col gap-1.5">
        <label htmlFor="product-description" className="text-sm font-semibold text-text-secondary">
          Description
        </label>
        <textarea
          id="product-description"
          value={value.description}
          onChange={(e) => onChange({ ...value, description: e.target.value })}
          rows={3}
          maxLength={500}
          className="w-full rounded-lg border border-border-strong bg-white px-3 py-2 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
        />
        <p className="min-h-[1rem] text-[13px] leading-4 text-text-muted" aria-hidden="true">
          {' '}
        </p>
      </div>

      <div className="flex items-start justify-between gap-4 pt-1">
        <div>
          <p className="text-sm font-semibold text-text-secondary">Track inventory</p>
          <p className="text-[13px] leading-4 text-text-muted">
            Deduct stock on sale and show this product in inventory.
          </p>
        </div>
        <button
          type="button"
          role="switch"
          aria-checked={value.trackInventory}
          aria-label="Track inventory"
          onClick={() => onChange({ ...value, trackInventory: !value.trackInventory })}
          className={cn(
            'relative mt-1 inline-flex h-6 w-11 shrink-0 rounded-full transition-colors',
            'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500',
            value.trackInventory ? 'bg-primary-600' : 'bg-border-strong',
          )}
        >
          <span
            className={cn(
              'absolute top-0.5 size-5 rounded-full bg-white shadow-sm transition-transform',
              value.trackInventory ? 'translate-x-[1.375rem]' : 'translate-x-0.5',
            )}
          />
        </button>
      </div>
    </div>
  )
}
