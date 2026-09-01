import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  Building2,
  Calculator,
  ClipboardList,
  MapPin,
  PackageX,
  Receipt,
  Scale,
  ShoppingCart,
  Tag,
  Users,
  Wallet,
  Warehouse,
} from 'lucide-react'
import { dashboardApi } from '../api/endpoints'
import { useAuth } from '../auth/AuthContext'
import { useCan } from '../lib/useCan'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { Button, Card, ErrorState, MetricCard, SkeletonCard } from '../components/ui'
import { businessTypeLabel } from '../lib/businessTypes'
import { formatMoney } from '../lib/format'

export default function DashboardPage() {
  const { user } = useAuth()
  const canManageRegisters = useCan('register:manage')
  const { data, isPending, isError, error, refetch } = useQuery({
    queryKey: ['dashboard'],
    queryFn: dashboardApi.get,
  })

  return (
    <DashboardLayout title="Dashboard">
      <div className="space-y-8">
        <section className="bg-welcome-tint rounded-2xl border border-primary-100 p-6">
          <h2 className="text-xl font-bold text-text-primary sm:text-2xl">
            Welcome back, {user?.firstName}
          </h2>
          <p className="mt-1 text-sm text-text-secondary">
            Here is where your business stands today.
          </p>
        </section>

        {isError ? (
          <ErrorState
            title="Could not load your dashboard"
            message={error instanceof Error ? error.message : 'Please try again in a moment.'}
            onRetry={() => refetch()}
          />
        ) : (
          <>
            <section aria-label="Today" className="space-y-3">
              <h3 className="text-sm font-semibold text-text-primary">Today</h3>
              <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
                {isPending ? (
                  <>
                    <SkeletonCard />
                    <SkeletonCard />
                    <SkeletonCard />
                    <SkeletonCard />
                  </>
                ) : (
                  <>
                    <MetricCard
                      icon={Wallet}
                      accent="green"
                      label="Today's sales"
                      value={formatMoney(data.todaysSales)}
                    />
                    <MetricCard
                      icon={Receipt}
                      accent="blue"
                      label="Transactions"
                      value={data.todaysTransactions}
                    />
                    <MetricCard
                      icon={Scale}
                      accent="purple"
                      label="Average transaction"
                      value={formatMoney(data.averageTransactionValue)}
                    />
                    <Link
                      to="/inventory"
                      className="block rounded-xl focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-primary-500"
                    >
                      <MetricCard
                        icon={PackageX}
                        accent={data.lowStockItems > 0 ? 'amber' : 'blue'}
                        label="Low stock items"
                        value={data.lowStockItems}
                      />
                    </Link>
                  </>
                )}
              </div>
            </section>

            <section aria-label="Quick actions" className="space-y-3">
              <h3 className="text-sm font-semibold text-text-primary">Quick actions</h3>
              <Card className="flex flex-wrap gap-2">
                <Link to="/pos">
                  <Button size="sm" leadingIcon={<ShoppingCart className="size-4" aria-hidden="true" />}>
                    Open POS
                  </Button>
                </Link>
                <Link to="/sales">
                  <Button
                    variant="secondary"
                    size="sm"
                    leadingIcon={<ClipboardList className="size-4" aria-hidden="true" />}
                  >
                    View sales
                  </Button>
                </Link>
                <Link to="/inventory">
                  <Button
                    variant="secondary"
                    size="sm"
                    leadingIcon={<Warehouse className="size-4" aria-hidden="true" />}
                  >
                    View inventory
                  </Button>
                </Link>
                {canManageRegisters && (
                  <Link to="/registers">
                    <Button
                      variant="secondary"
                      size="sm"
                      leadingIcon={<Calculator className="size-4" aria-hidden="true" />}
                    >
                      Manage registers
                    </Button>
                  </Link>
                )}
              </Card>
            </section>

            <section aria-label="Business overview" className="space-y-3">
              <h3 className="text-sm font-semibold text-text-primary">Business overview</h3>
              <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
                {isPending ? (
                  <>
                    <SkeletonCard />
                    <SkeletonCard />
                    <SkeletonCard />
                    <SkeletonCard />
                  </>
                ) : (
                  <>
                    <MetricCard
                      icon={Building2}
                      accent="blue"
                      label="Business"
                      value={data.tenant.name}
                    />
                    <MetricCard
                      icon={Tag}
                      accent="purple"
                      label="Business type"
                      value={businessTypeLabel(data.tenant.businessType)}
                    />
                    <MetricCard icon={MapPin} accent="orange" label="Branches" value={data.branchCount} />
                    <MetricCard icon={Users} accent="green" label="Users" value={data.userCount} />
                  </>
                )}
              </div>
            </section>
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
