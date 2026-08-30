import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { productsApi } from '../../api/catalog'
import { ApiError } from '../../api/client'
import type { CategoryDto, ProductDto } from '../../api/types'
import { buildUpdateProductRequest } from '../../lib/catalogRequests'
import { fieldErrorsFrom, mapCodeToField } from '../../lib/formErrors'
import { cn } from '../../lib/cn'
import { Button, Card, TextField, useToast } from '../ui'
import { ProductFields } from './ProductFields'
import type { ProductDetailsValue } from './ProductFields'

interface SingleFields {
  sku: string
  barcode: string
  costPrice: string
  sellingPrice: string
}

const CONFLICT_MESSAGES: Record<string, string> = {
  sku: 'A product with this SKU already exists.',
  barcode: 'A product with this barcode already exists.',
  name: 'A product with this name already exists.',
}

export function ProductEditForm({
  product,
  categories,
}: {
  product: ProductDto
  categories: CategoryDto[]
}): React.ReactNode {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const { toast } = useToast()

  const [details, setDetails] = useState<ProductDetailsValue>({
    categoryId: product.categoryId,
    name: product.name,
    description: product.description ?? '',
    trackInventory: product.trackInventory,
  })
  const [isActive, setIsActive] = useState(product.isActive)
  const [single, setSingle] = useState<SingleFields>({
    sku: product.sku ?? '',
    barcode: product.barcode ?? '',
    costPrice: String(product.minCostPrice ?? 0),
    sellingPrice: String(product.minSellingPrice),
  })
  const [errors, setErrors] = useState<Record<string, string>>({})

  // Represent the product's current category in the Select even if it has since been deactivated
  // (and so is absent from the active-categories feed).
  const categoriesWithCurrent: CategoryDto[] = categories.some((c) => c.id === product.categoryId)
    ? categories
    : [
        {
          id: product.categoryId,
          name: product.categoryName,
          description: null,
          isActive: false,
          productCount: 0,
          createdAtUtc: '',
          updatedAtUtc: '',
        },
        ...categories,
      ]

  const mutation = useMutation({
    mutationFn: () =>
      productsApi.update(
        product.id,
        buildUpdateProductRequest(product, {
          categoryId: details.categoryId,
          name: details.name.trim(),
          description: details.description.trim() || null,
          trackInventory: details.trackInventory,
          isActive,
          ...(product.hasVariants
            ? {}
            : {
                sku: single.sku.trim() || null,
                barcode: single.barcode.trim() || null,
                costPrice: Number(single.costPrice) || 0,
                sellingPrice: Number(single.sellingPrice) || 0,
              }),
        }),
      ),
    onSuccess: (detail) => {
      qc.invalidateQueries({ queryKey: ['products'] })
      qc.invalidateQueries({ queryKey: ['product', product.id] })
      toast('success', 'Changes saved')
      navigate(`/products/${detail.product.id}`)
    },
    onError: (error) => {
      const fields = fieldErrorsFrom(error)
      const codeField = error instanceof ApiError ? mapCodeToField(error.code) : null
      // A SKU/barcode/category conflict is only actionable inline for a simple product. A variant
      // product has no such fields, so it falls through to a toast instead of a silent dead-end.
      if (codeField && !product.hasVariants && !fields[codeField]) {
        fields[codeField] = CONFLICT_MESSAGES[codeField] ?? 'This value is already in use.'
      }
      setErrors(fields)
      if (Object.keys(fields).length === 0) {
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
    if (!product.hasVariants) {
      if (Number(single.costPrice) < 0) next.costprice = 'Cost price cannot be negative.'
      if (Number(single.sellingPrice) < 0) next.sellingprice = 'Selling price cannot be negative.'
    }

    setErrors(next)
    if (Object.keys(next).length > 0) return

    mutation.mutate()
  }

  return (
    <form onSubmit={submit} className="space-y-6">
      <Card>
        <h2 className="mb-3 text-sm font-semibold text-text-primary">Details</h2>
        <ProductFields
          value={details}
          onChange={setDetails}
          categories={categoriesWithCurrent}
          errors={errors}
        />
      </Card>

      <Card>
        <h2 className="text-sm font-semibold text-text-primary">Pricing &amp; codes</h2>
        {product.hasVariants ? (
          <p className="mt-2 text-[13px] text-text-muted">
            Pricing and codes are managed per variant on the product page.
          </p>
        ) : (
          <div className="mt-3 grid grid-cols-1 gap-x-4 sm:grid-cols-2">
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
        )}
      </Card>

      <Card>
        <div className="flex items-start justify-between gap-4">
          <div>
            <p className="text-sm font-semibold text-text-secondary">Active</p>
            <p className="text-[13px] leading-4 text-text-muted">
              Inactive products are hidden from the POS and product pickers.
            </p>
          </div>
          <button
            type="button"
            role="switch"
            aria-checked={isActive}
            aria-label="Active"
            onClick={() => setIsActive((v) => !v)}
            className={cn(
              'relative mt-1 inline-flex h-6 w-11 shrink-0 rounded-full transition-colors',
              'focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500',
              isActive ? 'bg-primary-600' : 'bg-border-strong',
            )}
          >
            <span
              className={cn(
                'absolute top-0.5 size-5 rounded-full bg-white shadow-sm transition-transform',
                isActive ? 'translate-x-[1.375rem]' : 'translate-x-0.5',
              )}
            />
          </button>
        </div>
      </Card>

      <div className="flex justify-end gap-2">
        <Button variant="secondary" onClick={() => navigate(`/products/${product.id}`)}>
          Cancel
        </Button>
        <Button type="submit" loading={mutation.isPending}>
          Save changes
        </Button>
      </div>
    </form>
  )
}
