import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Banknote, Calculator, Receipt, TrendingUp, Undo2 } from 'lucide-react'
import { reportsApi } from '../api/reports'
import { useAuth } from '../auth/AuthContext'
import { useReportFilters } from '../hooks/useReportFilters'
import { formatMoney, formatQty } from '../lib/format'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ReportFilterBar } from '../components/reports/ReportFilterBar'
import { SalesTrendChart } from '../components/reports/SalesTrendChart'
import { PaymentMethodBreakdown } from '../components/reports/PaymentMethodBreakdown'
import { TopProductsTable } from '../components/reports/TopProductsTable'
import { CategoryPerformanceTable } from '../components/reports/CategoryPerformanceTable'
import { Card, ErrorState, MetricCard, SkeletonCard } from '../components/ui'

export default function ReportsPage() {
  const { user } = useAuth()
  const filters = useReportFilters()
  const [top, setTop] = useState<10 | 20>(10)

  // Owner/Admin can pick a branch (or "All branches"); a Manager's data is already forced
  // server-side to their own branch, so showing them a picker that quietly does nothing would
  // just be confusing — the filter bar hides it for them entirely.
  const showBranchFilter = user?.role === 'Owner' || user?.role === 'Admin'

  const overview = useQuery({
    queryKey: ['reports', 'overview', filters.params],
    queryFn: () => reportsApi.overview(filters.params),
  })

  const topProducts = useQuery({
    queryKey: ['reports', 'top-products', filters.params, top],
    queryFn: () => reportsApi.topProducts(filters.params, top),
  })

  const categories = useQuery({
    queryKey: ['reports', 'categories', filters.params],
    queryFn: () => reportsApi.categories(filters.params),
  })

  const kpis = overview.data?.kpis
  const comparison = overview.data?.comparison

  return (
    <DashboardLayout title="Reports">
      <div className="space-y-6">
        <div>
          <h1 className="text-2xl font-bold text-text-primary">Reports</h1>
          <p className="mt-1 text-sm text-text-secondary">Track sales and business performance.</p>
        </div>

        <ReportFilterBar filters={filters} showBranchFilter={showBranchFilter} />

        {overview.isError ? (
          <ErrorState
            title="Could not load reports"
            message={overview.error instanceof Error ? overview.error.message : 'Please try again in a moment.'}
            onRetry={() => overview.refetch()}
          />
        ) : (
          <>
            <section aria-label="Key metrics" className="space-y-3">
              <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
                {overview.isPending || !kpis || !comparison ? (
                  Array.from({ length: 5 }).map((_, i) => <SkeletonCard key={i} />)
                ) : (
                  <>
                    <MetricCard
                      icon={Banknote}
                      accent="blue"
                      label="Gross sales"
                      value={formatMoney(kpis.grossSales)}
                      deltaPct={comparison.grossSalesChangePercent ?? undefined}
                      deltaLabel="vs previous period"
                    />
                    <MetricCard
                      icon={TrendingUp}
                      accent="green"
                      label="Net sales"
                      value={formatMoney(kpis.netSales)}
                      deltaPct={comparison.netSalesChangePercent ?? undefined}
                      deltaLabel="vs previous period"
                    />
                    <MetricCard
                      icon={Receipt}
                      accent="purple"
                      label="Transactions"
                      value={kpis.completedTransactions}
                      deltaPct={comparison.transactionsChangePercent ?? undefined}
                      deltaLabel="vs previous period"
                    />
                    <MetricCard
                      icon={Calculator}
                      accent="amber"
                      label="Average sale"
                      value={formatMoney(kpis.averageTransactionValue)}
                      deltaPct={comparison.averageTransactionChangePercent ?? undefined}
                      deltaLabel="vs previous period"
                    />
                    <MetricCard
                      icon={Undo2}
                      accent="red"
                      label="Returns"
                      value={formatMoney(kpis.returns)}
                      deltaPct={comparison.returnsChangePercent ?? undefined}
                      deltaLabel="vs previous period"
                    />
                  </>
                )}
              </div>

              {kpis && (
                <p className="text-[13px] text-text-muted">
                  Discounts {formatMoney(kpis.discounts)} · Tax {formatMoney(kpis.tax)} · Net
                  collected {formatMoney(kpis.netCollected)} · {formatQty(kpis.totalItemsSold)}{' '}
                  items sold
                  {kpis.voidedSalesCount > 0 &&
                    ` · ${kpis.voidedSalesCount} voided sale${kpis.voidedSalesCount === 1 ? '' : 's'} (${formatMoney(kpis.voidedSalesValue)}, excluded above)`}
                </p>
              )}
            </section>

            <section className="grid gap-4 lg:grid-cols-[2fr_1fr]">
              <Card padding="md">
                <h2 className="mb-3 text-sm font-semibold text-text-primary">Sales trend</h2>
                {overview.isPending ? (
                  <div className="h-[280px] animate-pulse rounded-lg bg-border-light" />
                ) : (
                  <SalesTrendChart points={overview.data?.trend ?? []} />
                )}
              </Card>
              <Card padding="md">
                <h2 className="mb-3 text-sm font-semibold text-text-primary">Payment methods</h2>
                {overview.isPending ? (
                  <div className="h-[280px] animate-pulse rounded-lg bg-border-light" />
                ) : (
                  <PaymentMethodBreakdown methods={overview.data?.paymentMethods ?? []} />
                )}
              </Card>
            </section>

            <section className="grid gap-4 lg:grid-cols-2">
              <Card padding="md">
                <TopProductsTable
                  products={topProducts.data}
                  top={top}
                  onTopChange={setTop}
                  isPending={topProducts.isPending}
                  isError={topProducts.isError}
                  onRetry={() => topProducts.refetch()}
                />
              </Card>
              <Card padding="md">
                <CategoryPerformanceTable
                  categories={categories.data}
                  isPending={categories.isPending}
                  isError={categories.isError}
                  onRetry={() => categories.refetch()}
                />
              </Card>
            </section>
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
