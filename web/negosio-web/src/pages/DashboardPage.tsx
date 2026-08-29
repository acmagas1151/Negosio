import { useQuery } from '@tanstack/react-query'
import {
  Activity,
  BarChart3,
  Building2,
  MapPin,
  Sparkles,
  Tag,
  Users,
} from 'lucide-react'
import { dashboardApi } from '../api/endpoints'
import { useAuth } from '../auth/AuthContext'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import {
  Card,
  EmptyState,
  ErrorState,
  MetricCard,
  SkeletonCard,
} from '../components/ui'
import { businessTypeLabel, nextSetupStep } from '../lib/businessTypes'

export default function DashboardPage() {
  const { user } = useAuth()
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
            Here is an overview of your business on Negosio.
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
            <section
              className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4"
              aria-label="Business overview"
            >
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
                    label="Business Type"
                    value={businessTypeLabel(data.tenant.businessType)}
                  />
                  <MetricCard
                    icon={MapPin}
                    accent="orange"
                    label="Branches"
                    value={data.branchCount}
                  />
                  <MetricCard
                    icon={Users}
                    accent="green"
                    label="Users"
                    value={data.userCount}
                  />
                </>
              )}
            </section>

            <section className="grid gap-4 lg:grid-cols-3">
              <Card className="lg:col-span-2">
                <div className="flex items-center gap-2">
                  <span className="flex size-8 items-center justify-center rounded-lg bg-primary-50 text-primary-600">
                    <Sparkles className="size-4" aria-hidden="true" />
                  </span>
                  <h3 className="text-sm font-semibold text-text-primary">Next setup step</h3>
                </div>
                <p className="mt-3 text-base font-semibold text-text-primary">
                  {data ? nextSetupStep(data.tenant.businessType) : 'Continue setting up your business'}
                </p>
                <p className="mt-1 text-[13px] text-text-muted">Coming in a later phase.</p>
              </Card>

              <Card>
                <div className="flex items-center gap-2">
                  <span className="flex size-8 items-center justify-center rounded-lg bg-purple-light text-purple">
                    <BarChart3 className="size-4" aria-hidden="true" />
                  </span>
                  <h3 className="text-sm font-semibold text-text-primary">Revenue analytics</h3>
                </div>
                <p className="mt-3 text-[13px] text-text-secondary">
                  Sales and revenue charts arrive with the POS phase.
                </p>
              </Card>
            </section>

            <section>
              <h3 className="mb-3 text-sm font-semibold text-text-primary">Recent activity</h3>
              <EmptyState
                icon={Activity}
                title="Nothing to show yet"
                description="Activity from sales, orders and inventory will appear here once those features are live."
              />
            </section>
          </>
        )}
      </div>
    </DashboardLayout>
  )
}
