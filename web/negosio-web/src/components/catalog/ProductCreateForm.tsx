import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate } from 'react-router-dom'
import { productsApi } from '../../api/catalog'
import { ApiError } from '../../api/client'
import type { CategoryDto, CreateProductRequest } from '../../api/types'
import { fieldErrorsFrom, mapCodeToField } from '../../lib/formErrors'
import { cn } from '../../lib/cn'
import { Button, Callout, Card, TextField, useToast } from '../ui'
import { ProductFields } from './ProductFields'
import type { ProductDetailsValue } from './ProductFields'
import { VariantRowsField } from './VariantRowsField'
import type { VariantRow } from './VariantRowsField'

type Mode = 'single' | 'variants'

interface SingleFields {
  sku: string
  barcode: string
  costPrice: string
  sellingPrice: string
}

const EMPTY_VARIANT_ROW: VariantRow = {
  name: '',
  sku: '',
  barcode: '',
  costPrice: '0',
  sellingPrice: '0',
}

const CONFLICT_MESSAGES: Record<string, string> = {
  sku: 'A product with this SKU already exists.',
  barcode: 'A product with this barcode already exists.',
  name: 'A product with this name already exists.',
}

// Field keys this form surfaces inline. A server field error outside this set (e.g. `description`,
// `variants[0].name`) would otherwise be invisible, so we also toast the message.
const INLINE_FIELDS = ['categoryid', 'name', 'sku', 'barcode', 'costprice', 'sellingprice', 'variants']

export function ProductCreateForm({ categories }: { categories: CategoryDto[] }): React.ReactNode {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const { toast } = useToast()

  const [details, setDetails] = useState<ProductDetailsValue>({
    categoryId: '',
    name: '',
    description: '',
    trackInventory: true,
  })
  const [mode, setMode] = useState<Mode>('single')
  const [single, setSingle] = useState<SingleFields>({
    sku: '',
    barcode: '',
    costPrice: '0',
    sellingPrice: '0',
  })
  const [rows, setRows] = useState<VariantRow[]>([{ ...EMPTY_VARIANT_ROW }])
  const [errors, setErrors] = useState<Record<string, string>>({})

  const noCategories = categories.length === 0

  const mutation = useMutation({
    mutationFn: () => {
      const base = {
        categoryId: details.categoryId,
        name: details.name.trim(),
        description: details.description.trim() || null,
        trackInventory: details.trackInventory,
      }
      const body: CreateProductRequest =
        mode === 'variants'
          ? {
              ...base,
              sku: null,
              barcode: null,
              costPrice: 0,
              sellingPrice: 0,
              variants: rows.map((r) => ({
                name: r.name.trim(),
                sku: r.sku.trim() || null,
                barcode: r.barcode.trim() || null,
                costPrice: Number(r.costPrice) || 0,
                sellingPrice: Number(r.sellingPrice) || 0,
              })),
            }
          : {
              ...base,
              sku: single.sku.trim() || null,
              barcode: single.barcode.trim() || null,
              costPrice: Number(single.costPrice) || 0,
              sellingPrice: Number(single.sellingPrice) || 0,
              variants: null,
            }
      return productsApi.create(body)
    },
    onSuccess: (detail) => {
      qc.invalidateQueries({ queryKey: ['products'] })
      toast('success', 'Product created')
      navigate(`/products/${detail.product.id}`)
    },
    onError: (error) => {
      const fields = fieldErrorsFrom(error)
      const codeField = error instanceof ApiError ? mapCodeToField(error.code) : null
      // In variants mode there is no inline sku/barcode field — surface the conflict on the
      // variants Callout (and a toast) so it isn't a silent dead-end.
      if (mode === 'variants' && (codeField === 'sku' || codeField === 'barcode')) {
        const message = error instanceof Error ? error.message : 'That value is already in use.'
        setErrors({ variants: message })
        toast('error', message)
        return
      }
      if (codeField && !fields[codeField]) {
        fields[codeField] = CONFLICT_MESSAGES[codeField] ?? 'This value is already in use.'
      }
      setErrors(fields)
      const keys = Object.keys(fields)
      if (keys.length === 0 || keys.some((k) => !INLINE_FIELDS.includes(k))) {
        toast('error', error instanceof Error ? error.message : 'Something went wrong.')
      }
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return

    const next: Record<string, string> = {}
    if (!details.categoryId) next.categoryid = 'Category is required.'
    if (!details.name.trim()) next.name = 'Name is required.'

    if (mode === 'single') {
      if (Number(single.costPrice) < 0) next.costprice = 'Cost price cannot be negative.'
      if (Number(single.sellingPrice) < 0) next.sellingprice = 'Selling price cannot be negative.'
    } else {
      if (rows.some((r) => !r.name.trim())) next.variants = 'Give every variant a name.'
      else if (rows.some((r) => Number(r.costPrice) < 0 || Number(r.sellingPrice) < 0))
        next.variants = 'Variant prices cannot be negative.'
    }

    setErrors(next)
    if (Object.keys(next).length > 0) {
      if (next.variants) toast('error', next.variants)
      return
    }

    mutation.mutate()
  }

  return (
    <form onSubmit={submit} className="space-y-6">
      {noCategories && (
        <Callout tone="warning">
          You need at least one category before you can add a product.{' '}
          <Link to="/categories">Create a category first.</Link>
        </Callout>
      )}

      <Card>
        <h2 className="mb-3 text-sm font-semibold text-text-primary">Details</h2>
        <ProductFields
          value={details}
          onChange={setDetails}
          categories={categories}
          errors={errors}
        />
      </Card>

      <Card>
        <h2 className="text-sm font-semibold text-text-primary">Pricing</h2>
        <p className="mt-1 text-[13px] text-text-muted">
          Sell this product as one item, or split it into variants (sizes, flavours, colours…).
        </p>

        <div className="mt-3 inline-flex rounded-lg border border-border-strong p-0.5">
          {(['single', 'variants'] as const).map((m) => (
            <button
              key={m}
              type="button"
              aria-pressed={mode === m}
              onClick={() => setMode(m)}
              className={cn(
                'rounded-md px-3 py-1.5 text-sm font-semibold transition-colors',
                mode === m
                  ? 'bg-primary-600 text-white'
                  : 'text-text-secondary hover:text-text-primary',
              )}
            >
              {m === 'single' ? 'Single item' : 'Has variants'}
            </button>
          ))}
        </div>

        <div className="mt-4">
          {mode === 'single' ? (
            <div className="grid grid-cols-1 gap-x-4 sm:grid-cols-2">
              <TextField
                label="SKU"
                name="sku"
                value={single.sku}
                maxLength={64}
                error={errors.sku || undefined}
                onChange={(e) => setSingle({ ...single, sku: e.target.value })}
              />
              <TextField
                label="Barcode"
                name="barcode"
                value={single.barcode}
                maxLength={64}
                error={errors.barcode || undefined}
                onChange={(e) => setSingle({ ...single, barcode: e.target.value })}
              />
              <TextField
                label="Cost price"
                name="costPrice"
                type="number"
                min={0}
                step="0.01"
                value={single.costPrice}
                error={errors.costprice || undefined}
                onChange={(e) => setSingle({ ...single, costPrice: e.target.value })}
              />
              <TextField
                label="Selling price"
                name="sellingPrice"
                type="number"
                min={0}
                step="0.01"
                value={single.sellingPrice}
                error={errors.sellingprice || undefined}
                onChange={(e) => setSingle({ ...single, sellingPrice: e.target.value })}
              />
            </div>
          ) : (
            <>
              {errors.variants && <Callout tone="error">{errors.variants}</Callout>}
              <VariantRowsField rows={rows} onChange={setRows} />
            </>
          )}
        </div>
      </Card>

      <div className="flex justify-end gap-2">
        <Button variant="secondary" onClick={() => navigate('/products')}>
          Cancel
        </Button>
        <Button type="submit" loading={mutation.isPending} disabled={noCategories}>
          Create product
        </Button>
      </div>
    </form>
  )
}
