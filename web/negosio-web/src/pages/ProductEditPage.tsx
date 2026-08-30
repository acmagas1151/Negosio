import { useQuery } from '@tanstack/react-query'
import { Navigate, useParams } from 'react-router-dom'
import { categoriesApi, productsApi } from '../api/catalog'
import { ApiError } from '../api/client'
import { useCan } from '../lib/useCan'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ProductEditForm } from '../components/catalog/ProductEditForm'
import { EmptyState, ErrorState, LoadingState } from '../components/ui'

export default function ProductEditPage() {
  const { id } = useParams<{ id: string }>()
  const canWrite = useCan('catalog:write')

  const product = useQuery({
    queryKey: ['product', id],
    queryFn: () => productsApi.get(id!),
    enabled: !!id,
  })
  const categories = useQuery({
    queryKey: ['categories', 'all-active'],
    queryFn: categoriesApi.listAllActive,
  })

  if (!canWrite) return <Navigate to={`/products/${id}`} replace />

  return (
    <DashboardLayout title="Edit product">
      <div className="mx-auto max-w-2xl">
        {product.isError &&
        product.error instanceof ApiError &&
        product.error.status === 404 ? (
          <EmptyState title="Product not found" description="It may have been removed." />
        ) : product.isError ? (
          <ErrorState
            message={product.error instanceof Error ? product.error.message : 'Please try again.'}
            onRetry={() => product.refetch()}
          />
        ) : categories.isError ? (
          <ErrorState
            message={
              categories.error instanceof Error ? categories.error.message : 'Please try again.'
            }
            onRetry={() => categories.refetch()}
          />
        ) : product.isPending || categories.isPending ? (
          <LoadingState />
        ) : (
          <ProductEditForm product={product.data.product} categories={categories.data} />
        )}
      </div>
    </DashboardLayout>
  )
}
