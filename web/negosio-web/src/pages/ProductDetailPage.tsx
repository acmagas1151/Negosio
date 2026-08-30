import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { PackageX } from 'lucide-react'
import { productsApi, variantsApi } from '../api/catalog'
import { ApiError } from '../api/client'
import type { ProductDto, ProductVariantDto } from '../api/types'
import { buildUpdateProductRequest } from '../lib/catalogRequests'
import { useCan } from '../lib/useCan'
import { formatMarginPct, formatMoney } from '../lib/format'
import { VariantFormModal } from '../components/catalog/VariantFormModal'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Badge,
  Button,
  Card,
  ConfirmDialog,
  EmptyState,
  ErrorState,
  LoadingState,
  Table,
  useToast,
} from '../components/ui'

export default function ProductDetailPage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const qc = useQueryClient()
  const { toast } = useToast()
  const canWrite = useCan('catalog:write')
  const canViewCost = useCan('costs:view')

  const [confirmOpen, setConfirmOpen] = useState(false)
  const [showInactive, setShowInactive] = useState(false)
  const [variantModal, setVariantModal] = useState<{
    open: boolean
    variant: ProductVariantDto | null
  }>({ open: false, variant: null })
  const [variantToDeactivate, setVariantToDeactivate] = useState<ProductVariantDto | null>(null)

  const detail = useQuery({
    queryKey: ['product', id],
    queryFn: () => productsApi.get(id!),
    enabled: !!id,
  })

  const invalidateAll = () => {
    qc.invalidateQueries({ queryKey: ['product', id] })
    qc.invalidateQueries({ queryKey: ['products'] })
  }

  const onMutationError = (error: unknown) =>
    toast('error', error instanceof Error ? error.message : 'Something went wrong.')

  const deactivateMutation = useMutation({
    mutationFn: () => productsApi.deactivate(id!),
    onSuccess: () => {
      invalidateAll()
      toast('success', 'Product deactivated')
      setConfirmOpen(false)
    },
    onError: onMutationError,
  })

  const reactivateMutation = useMutation({
    mutationFn: (product: ProductDto) =>
      productsApi.update(id!, buildUpdateProductRequest(product, { isActive: true })),
    onSuccess: () => {
      invalidateAll()
      toast('success', 'Product reactivated')
    },
    onError: onMutationError,
  })

  const deactivateVariantMutation = useMutation({
    mutationFn: (v: ProductVariantDto) => variantsApi.deactivate(id!, v.id),
    onSuccess: (_data, v) => {
      invalidateAll()
      toast('success', `${v.name} deactivated`)
      setVariantToDeactivate(null)
    },
    onError: onMutationError,
  })

  const busy = deactivateMutation.isPending || reactivateMutation.isPending

  if (detail.isError && detail.error instanceof ApiError && detail.error.status === 404) {
    return (
      <DashboardLayout title="Product">
        <EmptyState
          icon={PackageX}
          title="Product not found"
          description="It may have been removed."
          action={
            <Link
              to="/products"
              className="text-sm font-semibold text-primary-700 hover:underline"
            >
              ← Back to products
            </Link>
          }
        />
      </DashboardLayout>
    )
  }

  if (detail.isError) {
    return (
      <DashboardLayout title="Product">
        <ErrorState
          message={detail.error instanceof Error ? detail.error.message : 'Please try again.'}
          onRetry={() => detail.refetch()}
        />
      </DashboardLayout>
    )
  }

  if (detail.isPending) {
    return (
      <DashboardLayout title="Product">
        <LoadingState />
      </DashboardLayout>
    )
  }

  const { product, variants } = detail.data
  const rows = showInactive ? variants : variants.filter((v) => v.isActive)

  return (
    <DashboardLayout title="Product">
      <div className="space-y-6">
        <div>
          <Link
            to="/products"
            className="text-sm font-semibold text-primary-700 hover:underline"
          >
            ← Products
          </Link>
          <div className="mt-2 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
            <div className="flex flex-wrap items-center gap-3">
              <h1 className="text-2xl font-bold text-text-primary">{product.name}</h1>
              <Badge tone={product.isActive ? 'success' : 'neutral'}>
                {product.isActive ? 'Active' : 'Inactive'}
              </Badge>
            </div>
            {canWrite && (
              <div className="flex gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={() => navigate(`/products/${id}/edit`)}
                  disabled={busy}
                >
                  Edit
                </Button>
                {product.isActive ? (
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => setConfirmOpen(true)}
                    disabled={busy}
                  >
                    Deactivate
                  </Button>
                ) : (
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => reactivateMutation.mutate(product)}
                    disabled={busy}
                    loading={reactivateMutation.isPending}
                  >
                    Reactivate
                  </Button>
                )}
              </div>
            )}
          </div>
        </div>

        <Card>
          <h2 className="text-sm font-semibold text-text-primary">Details</h2>
          <dl className="mt-3 grid grid-cols-1 gap-x-6 gap-y-3 sm:grid-cols-2">
            <DetailRow label="Category" value={product.categoryName} />
            <DetailRow label="Description" value={product.description ?? '—'} />
            <DetailRow label="Track inventory" value={product.trackInventory ? 'Yes' : 'No'} />
            <DetailRow
              label="Created"
              value={new Date(product.createdAtUtc).toLocaleDateString()}
            />
            <DetailRow
              label="Last updated"
              value={new Date(product.updatedAtUtc).toLocaleDateString()}
            />
          </dl>
        </Card>

        <Card>
          {product.hasVariants ? (
            <>
              <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
                <h2 className="text-sm font-semibold text-text-primary">
                  Variants <span className="text-text-muted">({variants.length})</span>
                </h2>
                <div className="flex items-center gap-4">
                  <label className="flex items-center gap-2 text-[13px] text-text-secondary">
                    <input
                      type="checkbox"
                      checked={showInactive}
                      onChange={(e) => setShowInactive(e.target.checked)}
                      className="size-4 rounded border-border-strong text-primary-600"
                    />
                    Show inactive
                  </label>
                  {canWrite && (
                    <Button
                      size="sm"
                      onClick={() => setVariantModal({ open: true, variant: null })}
                    >
                      Add variant
                    </Button>
                  )}
                </div>
              </div>
              <div className="mt-3">
                {rows.length === 0 ? (
                  <p className="text-[13px] text-text-muted">No variants to display.</p>
                ) : (
                  <Table>
                    <Table.Head>
                      <Table.HeaderCell>Variant</Table.HeaderCell>
                      <Table.HeaderCell>SKU</Table.HeaderCell>
                      <Table.HeaderCell>Barcode</Table.HeaderCell>
                      <Table.HeaderCell align="right">Selling</Table.HeaderCell>
                      {canViewCost && <Table.HeaderCell align="right">Cost</Table.HeaderCell>}
                      {canViewCost && <Table.HeaderCell align="right">Margin</Table.HeaderCell>}
                      <Table.HeaderCell>Status</Table.HeaderCell>
                      {canWrite && <Table.HeaderCell align="right">Actions</Table.HeaderCell>}
                    </Table.Head>
                    <Table.Body>
                      {rows.map((v) => (
                        <Table.Row key={v.id}>
                          <Table.Cell className="font-medium text-text-primary">
                            <span className="flex flex-wrap items-center gap-2">
                              {v.name}
                              {v.isDefault && <Badge tone="neutral">Default</Badge>}
                            </span>
                          </Table.Cell>
                          <Table.Cell>{v.sku ?? '—'}</Table.Cell>
                          <Table.Cell>{v.barcode ?? '—'}</Table.Cell>
                          <Table.Cell align="right">{formatMoney(v.sellingPrice)}</Table.Cell>
                          {canViewCost && (
                            <Table.Cell align="right">
                              {v.costPrice == null ? '—' : formatMoney(v.costPrice)}
                            </Table.Cell>
                          )}
                          {canViewCost && (
                            <Table.Cell align="right">
                              {formatMarginPct(v.costPrice, v.sellingPrice) ?? '—'}
                            </Table.Cell>
                          )}
                          <Table.Cell>
                            <Badge tone={v.isActive ? 'success' : 'neutral'}>
                              {v.isActive ? 'Active' : 'Inactive'}
                            </Badge>
                          </Table.Cell>
                          {canWrite && (
                            <Table.Cell align="right">
                              <span className="flex justify-end gap-1">
                                <Button
                                  variant="ghost"
                                  size="sm"
                                  onClick={() => setVariantModal({ open: true, variant: v })}
                                >
                                  Edit
                                </Button>
                                {v.isActive && (
                                  <Button
                                    variant="ghost"
                                    size="sm"
                                    onClick={() => setVariantToDeactivate(v)}
                                  >
                                    Deactivate
                                  </Button>
                                )}
                              </span>
                            </Table.Cell>
                          )}
                        </Table.Row>
                      ))}
                    </Table.Body>
                  </Table>
                )}
              </div>
            </>
          ) : (
            <>
              <h2 className="text-sm font-semibold text-text-primary">Pricing</h2>
              <dl className="mt-3 grid grid-cols-1 gap-x-6 gap-y-3 sm:grid-cols-2">
                <DetailRow label="SKU" value={product.sku ?? '—'} />
                <DetailRow label="Barcode" value={product.barcode ?? '—'} />
                <DetailRow label="Selling price" value={formatMoney(product.minSellingPrice)} />
                {canViewCost && product.minCostPrice != null && (
                  <>
                    <DetailRow label="Cost price" value={formatMoney(product.minCostPrice)} />
                    <DetailRow
                      label="Margin"
                      value={formatMarginPct(product.minCostPrice, product.minSellingPrice) ?? '—'}
                    />
                  </>
                )}
              </dl>
              <p className="mt-4 text-[13px] text-text-muted">
                This product is sold as a single item. Add a variant to sell multiple versions
                (sizes, flavours, colours…).
              </p>
              {canWrite && (
                <div className="mt-4">
                  <Button
                    variant="secondary"
                    size="sm"
                    onClick={() => setVariantModal({ open: true, variant: null })}
                  >
                    Add variant
                  </Button>
                </div>
              )}
            </>
          )}
        </Card>
      </div>

      {canWrite && (
        <>
          <ConfirmDialog
            open={confirmOpen}
            onClose={() => setConfirmOpen(false)}
            onConfirm={() => deactivateMutation.mutate()}
            title="Deactivate product"
            message="This product will be hidden from the POS and product pickers. You can reactivate it later."
            confirmLabel="Deactivate"
            tone="danger"
            loading={deactivateMutation.isPending}
          />
          <ConfirmDialog
            open={variantToDeactivate !== null}
            onClose={() => setVariantToDeactivate(null)}
            onConfirm={() =>
              variantToDeactivate && deactivateVariantMutation.mutate(variantToDeactivate)
            }
            title="Deactivate variant"
            message={`"${variantToDeactivate?.name ?? ''}" will be hidden from the POS and product pickers. You can add it again later.`}
            confirmLabel="Deactivate"
            tone="danger"
            loading={deactivateVariantMutation.isPending}
          />
          <VariantFormModal
            open={variantModal.open}
            onClose={() => setVariantModal({ open: false, variant: null })}
            productId={id!}
            variant={variantModal.variant}
          />
        </>
      )}
    </DashboardLayout>
  )
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-[11px] font-semibold uppercase tracking-wide text-text-muted">
        {label}
      </dt>
      <dd className="mt-0.5 text-sm text-text-secondary">{value}</dd>
    </div>
  )
}
