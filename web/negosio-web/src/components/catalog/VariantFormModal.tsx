import { useEffect, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { variantsApi } from '../../api/catalog'
import { ApiError } from '../../api/client'
import type { ProductVariantDto, VariantInput } from '../../api/types'
import { fieldErrorsFrom, mapCodeToField } from '../../lib/formErrors'
import { Button, Modal, TextField, useToast } from '../ui'

interface Props {
  open: boolean
  onClose: () => void
  productId: string
  variant: ProductVariantDto | null
}

export function VariantFormModal({ open, onClose, productId, variant }: Props) {
  const qc = useQueryClient()
  const { toast } = useToast()
  const [name, setName] = useState('')
  const [sku, setSku] = useState('')
  const [barcode, setBarcode] = useState('')
  const [cost, setCost] = useState('0')
  const [selling, setSelling] = useState('0')
  const [nameError, setNameError] = useState('')
  const [skuError, setSkuError] = useState('')
  const [barcodeError, setBarcodeError] = useState('')

  // Re-seed the form whenever it opens for a different variant (or for create).
  useEffect(() => {
    if (!open) return
    // oxlint-disable-next-line set-state-in-effect
    setName(variant?.name ?? '')
    setSku(variant?.sku ?? '')
    setBarcode(variant?.barcode ?? '')
    setCost(variant?.costPrice != null ? String(variant.costPrice) : '0')
    setSelling(variant?.sellingPrice != null ? String(variant.sellingPrice) : '0')
    setNameError('')
    setSkuError('')
    setBarcodeError('')
  }, [open, variant])

  const mutation = useMutation({
    mutationFn: () => {
      const body: VariantInput = {
        name: name.trim(),
        sku: sku.trim() || null,
        barcode: barcode.trim() || null,
        costPrice: Number(cost) || 0,
        sellingPrice: Number(selling) || 0,
      }
      return variant
        ? variantsApi.update(productId, variant.id, body)
        : variantsApi.create(productId, body)
    },
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['product', productId] })
      qc.invalidateQueries({ queryKey: ['products'] })
      toast('success', variant ? 'Changes saved' : 'Variant created')
      onClose()
    },
    onError: (error) => {
      const fields = fieldErrorsFrom(error)
      const codeField = error instanceof ApiError ? mapCodeToField(error.code) : null
      if (fields.name) setNameError(fields.name)
      else if (fields.sku || codeField === 'sku')
        setSkuError(fields.sku ?? 'A variant with this SKU already exists.')
      else if (fields.barcode || codeField === 'barcode')
        setBarcodeError(fields.barcode ?? 'A variant with this barcode already exists.')
      else toast('error', error instanceof Error ? error.message : 'Something went wrong.')
    },
  })

  const submit = (e: React.FormEvent) => {
    if (mutation.isPending) return
    e.preventDefault()
    setNameError('')
    setSkuError('')
    setBarcodeError('')
    if (!name.trim()) {
      setNameError('Name is required.')
      return
    }
    if (Number(cost) < 0) {
      toast('error', 'Cost price cannot be negative.')
      return
    }
    if (Number(selling) < 0) {
      toast('error', 'Selling price cannot be negative.')
      return
    }
    mutation.mutate()
  }

  return (
    <Modal
      open={open}
      onClose={onClose}
      title={variant ? 'Edit variant' : 'New variant'}
      footer={
        <>
          <Button variant="secondary" size="sm" onClick={onClose} disabled={mutation.isPending}>
            Cancel
          </Button>
          <Button size="sm" onClick={submit} loading={mutation.isPending}>
            {variant ? 'Save' : 'Create'}
          </Button>
        </>
      }
    >
      <form onSubmit={submit} className="space-y-1">
        <TextField
          label="Name"
          name="name"
          value={name}
          maxLength={80}
          onChange={(e) => setName(e.target.value)}
          error={nameError || undefined}
          autoFocus
        />
        <TextField
          label="SKU"
          name="sku"
          value={sku}
          maxLength={64}
          onChange={(e) => setSku(e.target.value)}
          error={skuError || undefined}
        />
        <TextField
          label="Barcode"
          name="barcode"
          value={barcode}
          maxLength={64}
          onChange={(e) => setBarcode(e.target.value)}
          error={barcodeError || undefined}
        />
        <TextField
          label="Cost price"
          name="costPrice"
          type="number"
          min={0}
          step="0.01"
          value={cost}
          onChange={(e) => setCost(e.target.value)}
        />
        <TextField
          label="Selling price"
          name="sellingPrice"
          type="number"
          min={0}
          step="0.01"
          value={selling}
          onChange={(e) => setSelling(e.target.value)}
        />
      </form>
    </Modal>
  )
}
