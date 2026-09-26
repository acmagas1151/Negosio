import type { ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { useSearchParams } from 'react-router-dom'
import { reportsApi } from '../api/reports'
import { useAuth } from '../auth/AuthContext'
import { useReportFilters } from '../hooks/useReportFilters'
import type { ReportFiltersState } from '../hooks/useReportFilters'
import { formatMoney } from '../lib/format'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ReportFilterBar } from '../components/reports/ReportFilterBar'
import { EmptyState, ErrorState, SkeletonText, Table } from '../components/ui'

type Tab = 'branches' | 'registers' | 'cashiers'

function TabButton({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className={`rounded-md px-3 py-1.5 text-[13px] font-semibold ${active ? 'bg-white text-text-primary shadow-sm' : 'text-text-muted'}`}
    >
      {children}
    </button>
  )
}

export default function PerformanceReportsPage() {
  const { user } = useAuth()
  const [params, setParams] = useSearchParams()
  const tab: Tab = params.get('tab') === 'registers' ? 'registers' : params.get('tab') === 'cashiers' ? 'cashiers' : 'branches'
  const setTab = (next: Tab) => {
    setParams((prev) => {
      const p = new URLSearchParams(prev)
      if (next === 'branches') p.delete('tab')
      else p.set('tab', next)
      return p
    })
  }

  const filters = useReportFilters()

  // Owner/Admin can pick a branch (or "All branches"); a Manager's data is already forced
  // server-side to their own branch, so showing them a picker that quietly does nothing would
  // just be confusing — same convention as the Reports Overview page's filter bar.
  const showBranchFilter = user?.role === 'Owner' || user?.role === 'Admin'

  return (
    <DashboardLayout title="Performance Reports">
      <div className="space-y-5">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <h1 className="text-2xl font-bold text-text-primary">Performance Reports</h1>
          <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
            <TabButton active={tab === 'branches'} onClick={() => setTab('branches')}>
              Branches
            </TabButton>
            <TabButton active={tab === 'registers'} onClick={() => setTab('registers')}>
              Registers
            </TabButton>
            <TabButton active={tab === 'cashiers'} onClick={() => setTab('cashiers')}>
              Cashiers
            </TabButton>
          </div>
        </div>

        <ReportFilterBar filters={filters} showBranchFilter={showBranchFilter} />

        {tab === 'branches' && <BranchPerformanceTab filters={filters} />}
      </div>
    </DashboardLayout>
  )
}

function BranchPerformanceTab({ filters }: { filters: ReportFiltersState }) {
  const query = useQuery({
    queryKey: ['reports', 'branch-performance', filters.params],
    queryFn: () => reportsApi.branchPerformance(filters.params),
  })

  if (query.isError) {
    return (
      <ErrorState
        message={query.error instanceof Error ? query.error.message : 'Could not load branch performance.'}
        onRetry={() => query.refetch()}
      />
    )
  }

  if (query.isPending) {
    return (
      <Table>
        <Table.Head>
          <Table.HeaderCell>Branch</Table.HeaderCell>
          <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
          <Table.HeaderCell align="right">Gross sales</Table.HeaderCell>
          <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
          <Table.HeaderCell align="right">Average sale</Table.HeaderCell>
          <Table.HeaderCell align="right">Discounts</Table.HeaderCell>
          <Table.HeaderCell align="right">Returns</Table.HeaderCell>
          <Table.HeaderCell align="right">Voids</Table.HeaderCell>
        </Table.Head>
        <Table.Body>
          {Array.from({ length: 4 }).map((_, i) => (
            <Table.Row key={i}>
              {Array.from({ length: 8 }).map((__, j) => (
                <Table.Cell key={j}>
                  <SkeletonText className={j === 0 ? 'w-24' : 'w-16'} />
                </Table.Cell>
              ))}
            </Table.Row>
          ))}
        </Table.Body>
      </Table>
    )
  }

  if (query.data.rows.length === 0) {
    return <EmptyState title="No branch activity" description="No sales in this date range." />
  }

  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Branch</Table.HeaderCell>
        <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
        <Table.HeaderCell align="right">Gross sales</Table.HeaderCell>
        <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
        <Table.HeaderCell align="right">Average sale</Table.HeaderCell>
        <Table.HeaderCell align="right">Discounts</Table.HeaderCell>
        <Table.HeaderCell align="right">Returns</Table.HeaderCell>
        <Table.HeaderCell align="right">Voids</Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {query.data.rows.map((r) => (
          <Table.Row key={r.branchId}>
            <Table.Cell>{r.branchName}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.netSales)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.grossSales)}</Table.Cell>
            <Table.Cell align="right">{r.completedTransactions}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.averageTransactionValue)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.discounts)}</Table.Cell>
            <Table.Cell align="right">
              {r.returnsCount} ({formatMoney(r.returnsValue)})
            </Table.Cell>
            <Table.Cell align="right">
              {r.voidedSalesCount} ({formatMoney(r.voidedSalesValue)})
            </Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}
