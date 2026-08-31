import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { productsApi } from '../../api/catalog'
import { inventoryApi } from '../../api/inventory'
import { ApiError } from '../../api/client'
import type { AdjustInventoryRequest, BranchDto } from '../../api/types'
import { Button, Callout, Modal, Select, TextField, useToast } from '../ui'
import { cn } from '../../lib/cn'

interface Props {
  open: boolean
  onClose: () => void
  branches: BranchDto[]
}

const OPENING_STOCK_FETCH_LIMIT = 100

export function OpeningStockModal({ open, onClose, branches }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()

  const [branchId, setBranchId] = useState('')
  const [productId, setProductId] = useState('')
  const [variantId, setVariantId] = useState('')
  const [quantity, setQuantity] = useState('')
  const [reorderLevel, setReorderLevel] = useState('0')
  const [reason, setReason] = useState('Opening stock count')
  const [error, setError] = useState('')

  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setBranchId(branches.length === 1 ? branches[0].id : '')
    setProductId('')
    setVariantId('')
    setQuantity('')
    setReorderLevel('0')
    setReason('Opening stock count')
    setError('')
  }, [open, branches])

  const productsQuery = useQuery({
    queryKey: ['products', 'inventory-picker'],
    queryFn: () =>
      productsApi.list({
        isActive: true,
        trackInventory: true,
        pageSize: OPENING_STOCK_FETCH_LIMIT,
        sortBy: 'name',
      }),
    enabled: open,
  })

  const selectedProduct = productsQuery.data?.items.find((p) => p.id === productId)

  const detailQuery = useQuery({
    queryKey: ['product', productId],
    queryFn: () => productsApi.get(productId),
    enabled: open && !!productId && !!selectedProduct?.hasVariants,
  })

  const mutation = useMutation({
    mutationFn: () => {
      const body: AdjustInventoryRequest = {
        branchId,
        productId,
        productVariantId: selectedProduct?.hasVariants ? variantId : null,
        adjustment: Number(quantity),
        reason: reason.trim(),
        reorderLevel: reorderLevel.trim() === '' ? 0 : Number(reorderLevel),
        expectedConcurrencyToken: null,
      }
      return inventoryApi.adjust(body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['inventory'] })
      toast('success', 'Opening stock recorded')
      onClose()
    },
    onError: (err) => {
      if (err instanceof ApiError && err.code === 'INVALID_INVENTORY_ADJUSTMENT') {
        setError('This product already has stock at this branch — use Adjust from the list instead.')
        return
      }
      if (err instanceof ApiError) {
        setError(err.message)
        return
      }
      toast('error', 'Something went wrong.')
    },
  })

  const submit = (e: React.FormEvent) => {
    e.preventDefault()
    if (mutation.isPending) return
    setError('')

    if (!branchId) return setError('Choose a branch.')
    if (!productId) return setError('Choose a product.')
    if (selectedProduct?.hasVariants && !variantId) return setError('Choose a variant.')
    if (!(Number(quantity) > 0)) return setError('Enter an opening quantity greater than zero.')
    if (!reason.trim()) return setError('A reason is required.')
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title="Add opening stock"
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            Record opening stock
          </Button>
        </>
      }
    >
      {error && <Callout tone="error">{error}</Callout>}

      <form onSubmit={submit} className="space-y-1">
        {branches.length > 1 && (
          <Select
            label="Branch"
            value={branchId}
            onChange={(e) => setBranchId(e.target.value)}
          >
            <option value="">Select a branch</option>
            {branches.map((b) => (
              <option key={b.id} value={b.id}>
                {b.name}
              </option>
            ))}
          </Select>
        )}

        <Select
          label="Product"
          value={productId}
          disabled={productsQuery.isPending}
          onChange={(e) => {
            setProductId(e.target.value)
            setVariantId('')
          }}
        >
          <option value="">
            {productsQuery.isPending ? 'Loading products…' : 'Select a product'}
          </option>
          {productsQuery.data?.items.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>

        {selectedProduct?.hasVariants && (
          <Select
            label="Variant"
            value={variantId}
            disabled={detailQuery.isPending}
            onChange={(e) => setVariantId(e.target.value)}
          >
            <option value="">
              {detailQuery.isPending ? 'Loading variants…' : 'Select a variant'}
            </option>
            {detailQuery.data?.variants
              .filter((v) => v.isActive)
              .map((v) => (
                <option key={v.id} value={v.id}>
                  {v.name}
                  {v.sku ? ` (${v.sku})` : ''}
                </option>
              ))}
          </Select>
        )}

        {productsQuery.data && productsQuery.data.items.length === 0 && (
          <p className="text-[13px] text-text-muted">
            No stock-tracked products yet. Create a product with inventory tracking first.
          </p>
        )}

        <TextField
          label="Opening quantity"
          name="quantity"
          type="number"
          min={0}
          step="0.001"
          value={quantity}
          onChange={(e) => setQuantity(e.target.value)}
        />

        <TextField
          label="Reorder level"
          name="reorderLevel"
          type="number"
          min={0}
          step="0.001"
          value={reorderLevel}
          onChange={(e) => setReorderLevel(e.target.value)}
          hint="Stock at or below this level is flagged as low."
        />

        <div className="flex flex-col gap-1.5">
          <label className="text-sm font-semibold text-text-secondary" htmlFor="opening-reason">
            Reason
          </label>
          <input
            id="opening-reason"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={500}
            className={cn(
              'h-11 w-full rounded-lg border border-border-strong bg-white px-3 text-sm text-text-primary',
              'focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15',
            )}
          />
        </div>
      </form>
    </Modal>
  )
}
