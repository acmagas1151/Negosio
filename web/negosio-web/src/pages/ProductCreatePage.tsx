import { useQuery } from '@tanstack/react-query'
import { Navigate } from 'react-router-dom'
import { categoriesApi } from '../api/catalog'
import { useCan } from '../lib/useCan'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ProductCreateForm } from '../components/catalog/ProductCreateForm'
import { ErrorState, LoadingState } from '../components/ui'

export default function ProductCreatePage() {
  const canWrite = useCan('catalog:write')
  const categories = useQuery({
    queryKey: ['categories', 'all-active'],
    queryFn: categoriesApi.listAllActive,
  })

  if (!canWrite) return <Navigate to="/products" replace />

  return (
    <DashboardLayout title="New product">
      <div className="mx-auto max-w-2xl">
        {categories.isError ? (
          <ErrorState
            message={(categories.error as Error).message}
            onRetry={() => categories.refetch()}
          />
        ) : categories.isPending ? (
          <LoadingState />
        ) : (
          <ProductCreateForm categories={categories.data} />
        )}
      </div>
    </DashboardLayout>
  )
}
