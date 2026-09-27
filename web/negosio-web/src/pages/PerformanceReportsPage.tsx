import type { ReactNode } from 'react'
import { useEffect, useMemo, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link, useSearchParams } from 'react-router-dom'
import { reportsApi } from '../api/reports'
import { useAuth } from '../auth/AuthContext'
import { useReportFilters } from '../hooks/useReportFilters'
import type { ReportFiltersState } from '../hooks/useReportFilters'
import { formatMoney } from '../lib/format'
import { buildSalesDrilldownUrl } from '../lib/reportsDrilldown'
import { DashboardLayout } from '../components/layout/DashboardLayout'
import { ReportFilterBar } from '../components/reports/ReportFilterBar'
import { EmptyState, ErrorState, Pagination, SkeletonText, Table } from '../components/ui'
import type { BranchPerformanceRowDto, CashierPerformanceRowDto, RegisterPerformanceRowDto } from '../api/types'

type Tab = 'branches' | 'registers' | 'cashiers'

// ---- Client-side sort for the three flat comparison tables (Branch/Register-sales/Cashier) — these
// views already fetch their full row array via a plain useQuery (no pagination), so sorting is done
// entirely in-memory rather than round-tripping to the server. Deliberately its own local state, not
// URL-synced like ProductsPage's server-side sort (usePagedQuery.setSort) — there's no page to preserve
// across. Toggle semantics mirror usePagedQuery.setSort exactly: click an unsorted column -> ascending,
// click it again -> descending, click it a third time -> clear back to the backend's own default order
// (NetSales descending).
type SortDirection = 'asc' | 'desc'
type SortState = { key: string; direction: SortDirection } | null

function toggleSort(current: SortState, key: string): SortState {
  if (!current || current.key !== key) return { key, direction: 'asc' }
  if (current.direction === 'asc') return { key, direction: 'desc' }
  return null
}

function useSortableRows<T>(rows: T[] | undefined, accessors: Record<string, (row: T) => number>) {
  const [sort, setSort] = useState<SortState>(null)
  const onSort = (key: string) => setSort((prev) => toggleSort(prev, key))
  const sortedRows = useMemo(() => {
    const list = rows ?? []
    if (!sort) return list
    const accessor = accessors[sort.key]
    if (!accessor) return list
    const dir = sort.direction === 'asc' ? 1 : -1
    return [...list].sort((a, b) => (accessor(a) - accessor(b)) * dir)
  }, [rows, sort, accessors])
  return { sortedRows, activeSort: { by: sort?.key, dir: sort?.direction }, onSort }
}

const branchSortAccessors: Record<string, (r: BranchPerformanceRowDto) => number> = {
  netSales: (r) => r.netSales,
  grossSales: (r) => r.grossSales,
  transactions: (r) => r.completedTransactions,
  averageSale: (r) => r.averageTransactionValue,
  discounts: (r) => r.discounts,
  returnsValue: (r) => r.returnsValue,
  voidedValue: (r) => r.voidedSalesValue,
}

const registerSalesSortAccessors: Record<string, (r: RegisterPerformanceRowDto) => number> = {
  netSales: (r) => r.netSales,
  transactions: (r) => r.completedTransactions,
  cashIn: (r) => r.cashIn,
  cashOut: (r) => r.cashOut,
}

const cashierSortAccessors: Record<string, (r: CashierPerformanceRowDto) => number> = {
  netSales: (r) => r.netSales,
  grossSales: (r) => r.grossSales,
  transactions: (r) => r.completedTransactions,
  averageSale: (r) => r.averageTransactionValue,
  discounts: (r) => r.discounts,
  returnsValue: (r) => r.returnsValue,
  voidedValue: (r) => r.voidedSalesValue,
  voidApprovalsCount: (r) => r.voidApprovalsCount,
  returnApprovalsCount: (r) => r.returnApprovalsCount,
  fulfillmentCancelApprovalsCount: (r) => r.fulfillmentCancelApprovalsCount,
}

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
        {tab === 'registers' && <RegisterPerformanceTab filters={filters} />}
        {tab === 'cashiers' && <CashierPerformanceTab filters={filters} />}
      </div>
    </DashboardLayout>
  )
}

function BranchPerformanceTab({ filters }: { filters: ReportFiltersState }) {
  const query = useQuery({
    queryKey: ['reports', 'branch-performance', filters.params],
    queryFn: () => reportsApi.branchPerformance(filters.params),
  })
  const { sortedRows, activeSort, onSort } = useSortableRows(query.data?.rows, branchSortAccessors)

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
        <Table.HeaderCell align="right" sortKey="netSales" activeSort={activeSort} onSort={onSort}>
          Net sales
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="grossSales" activeSort={activeSort} onSort={onSort}>
          Gross sales
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="transactions" activeSort={activeSort} onSort={onSort}>
          Transactions
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="averageSale" activeSort={activeSort} onSort={onSort}>
          Average sale
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="discounts" activeSort={activeSort} onSort={onSort}>
          Discounts
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="returnsValue" activeSort={activeSort} onSort={onSort}>
          Returns
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="voidedValue" activeSort={activeSort} onSort={onSort}>
          Voids
        </Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {sortedRows.map((r) => (
          <Table.Row key={r.branchId}>
            <Table.Cell>
              <Link
                to={buildSalesDrilldownUrl({ fromUtc: query.data.fromUtc, toUtc: query.data.toUtc, branchId: r.branchId })}
                className="font-semibold text-primary-700 hover:underline"
              >
                {r.branchName}
              </Link>
            </Table.Cell>
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

// ---- Registers tab: two granularities — a day-range sales/cash aggregate, and a paged list of
// individually closed sessions with their reconciliation figures. Kept as sibling sub-views rather
// than one table, same as the plan intends (Sales vs. Closed sessions are different row shapes).

function RegisterPerformanceTab({ filters }: { filters: ReportFiltersState }) {
  const [view, setView] = useState<'sales' | 'sessions'>('sales')

  return (
    <div className="space-y-5">
      <div className="flex justify-end">
        <div className="flex gap-1 rounded-lg border border-border-strong bg-surface-subtle p-1">
          <TabButton active={view === 'sales'} onClick={() => setView('sales')}>
            Sales
          </TabButton>
          <TabButton active={view === 'sessions'} onClick={() => setView('sessions')}>
            Closed sessions
          </TabButton>
        </div>
      </div>

      {view === 'sales' ? <RegisterSalesView filters={filters} /> : <RegisterSessionsView filters={filters} />}
    </div>
  )
}

function RegisterSalesView({ filters }: { filters: ReportFiltersState }) {
  const query = useQuery({
    queryKey: ['reports', 'register-performance', filters.params],
    queryFn: () => reportsApi.registerPerformance(filters.params),
  })
  const { sortedRows, activeSort, onSort } = useSortableRows(query.data?.rows, registerSalesSortAccessors)

  if (query.isError) {
    return (
      <ErrorState
        message={query.error instanceof Error ? query.error.message : 'Could not load register performance.'}
        onRetry={() => query.refetch()}
      />
    )
  }

  if (query.isPending) {
    return (
      <Table>
        <Table.Head>
          <Table.HeaderCell>Register</Table.HeaderCell>
          <Table.HeaderCell>Branch</Table.HeaderCell>
          <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
          <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
          <Table.HeaderCell>Payment methods</Table.HeaderCell>
          <Table.HeaderCell align="right">Cash in</Table.HeaderCell>
          <Table.HeaderCell align="right">Cash out</Table.HeaderCell>
        </Table.Head>
        <Table.Body>
          {Array.from({ length: 4 }).map((_, i) => (
            <Table.Row key={i}>
              {Array.from({ length: 7 }).map((__, j) => (
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
    return <EmptyState title="No register activity" description="No sales in this date range." />
  }

  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Register</Table.HeaderCell>
        <Table.HeaderCell>Branch</Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="netSales" activeSort={activeSort} onSort={onSort}>
          Net sales
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="transactions" activeSort={activeSort} onSort={onSort}>
          Transactions
        </Table.HeaderCell>
        <Table.HeaderCell>Payment methods</Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="cashIn" activeSort={activeSort} onSort={onSort}>
          Cash in
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="cashOut" activeSort={activeSort} onSort={onSort}>
          Cash out
        </Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {sortedRows.map((r) => (
          <Table.Row key={r.registerId}>
            <Table.Cell>
              <Link
                to={buildSalesDrilldownUrl({
                  fromUtc: query.data.fromUtc,
                  toUtc: query.data.toUtc,
                  branchId: r.branchId,
                  registerId: r.registerId,
                })}
                className="font-semibold text-primary-700 hover:underline"
              >
                {r.registerName}
              </Link>
            </Table.Cell>
            <Table.Cell>{r.branchName}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.netSales)}</Table.Cell>
            <Table.Cell align="right">{r.completedTransactions}</Table.Cell>
            <Table.Cell>
              {r.paymentMethods.length === 0
                ? '—'
                : r.paymentMethods.map((m) => `${m.method} ${formatMoney(m.amount)}`).join(' · ')}
            </Table.Cell>
            <Table.Cell align="right">{formatMoney(r.cashIn)}</Table.Cell>
            <Table.Cell align="right">{formatMoney(r.cashOut)}</Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}

function RegisterSessionsView({ filters }: { filters: ReportFiltersState }) {
  const [page, setPage] = useState(1)
  const pageSize = 20

  // A filter change can leave `page` pointing past the new result set's last page (e.g. narrowing
  // the date range while on page 3) — reset to page 1 whenever the shared filters change.
  useEffect(() => {
    setPage(1)
  }, [filters.period, filters.fromDate, filters.toDate, filters.branchId, filters.registerId, filters.cashierId])

  const query = useQuery({
    queryKey: ['reports', 'register-sessions', filters.params, page],
    queryFn: () => reportsApi.registerSessions({ ...filters.params, page, pageSize }),
  })

  if (query.isError) {
    return (
      <ErrorState
        message={query.error instanceof Error ? query.error.message : 'Could not load session reconciliation.'}
        onRetry={() => query.refetch()}
      />
    )
  }

  if (query.isPending) {
    return (
      <Table>
        <Table.Head>
          <Table.HeaderCell>Register</Table.HeaderCell>
          <Table.HeaderCell>Closed</Table.HeaderCell>
          <Table.HeaderCell>Closed by</Table.HeaderCell>
          <Table.HeaderCell align="right">Opening cash</Table.HeaderCell>
          <Table.HeaderCell align="right">Closing cash</Table.HeaderCell>
          <Table.HeaderCell align="right">Expected</Table.HeaderCell>
          <Table.HeaderCell align="right">Difference</Table.HeaderCell>
        </Table.Head>
        <Table.Body>
          {Array.from({ length: 4 }).map((_, i) => (
            <Table.Row key={i}>
              {Array.from({ length: 7 }).map((__, j) => (
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

  if (query.data.items.length === 0) {
    return <EmptyState title="No closed sessions" description="No sessions were closed in this date range." />
  }

  return (
    <>
      <Table>
        <Table.Head>
          <Table.HeaderCell>Register</Table.HeaderCell>
          <Table.HeaderCell>Closed</Table.HeaderCell>
          <Table.HeaderCell>Closed by</Table.HeaderCell>
          <Table.HeaderCell align="right">Opening cash</Table.HeaderCell>
          <Table.HeaderCell align="right">Closing cash</Table.HeaderCell>
          <Table.HeaderCell align="right">Expected</Table.HeaderCell>
          <Table.HeaderCell align="right">Difference</Table.HeaderCell>
        </Table.Head>
        <Table.Body>
          {query.data.items.map((r) => (
            <Table.Row key={r.sessionId}>
              <Table.Cell>
                <Link
                  to={buildSalesDrilldownUrl({ fromUtc: r.openedAtUtc, toUtc: r.closedAtUtc, registerId: r.registerId, branchId: r.branchId })}
                  className="font-semibold text-primary-700 hover:underline"
                >
                  {r.registerName}
                </Link>
              </Table.Cell>
              <Table.Cell>{new Date(r.closedAtUtc).toLocaleString()}</Table.Cell>
              <Table.Cell>{r.closedByName}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.openingCash)}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.closingCash)}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.expectedCash)}</Table.Cell>
              <Table.Cell align="right">{formatMoney(r.cashDifference)}</Table.Cell>
            </Table.Row>
          ))}
        </Table.Body>
      </Table>
      <Pagination
        page={query.data.page}
        pageSize={query.data.pageSize}
        totalCount={query.data.totalCount}
        totalPages={query.data.totalPages}
        onPageChange={setPage}
      />
    </>
  )
}

// ---- Cashiers tab: a single flat table, grouped by whoever actually performed each action
// (checkout/void/return), not by role — a Manager or Owner who personally rings up a sale shows up
// here exactly like a Cashier would, by design. No "Approved by" column for discounts: this
// codebase doesn't persist who approved one, so that identity is never implied here either.

function CashierPerformanceTab({ filters }: { filters: ReportFiltersState }) {
  const query = useQuery({
    queryKey: ['reports', 'cashier-performance', filters.params],
    queryFn: () => reportsApi.cashierPerformance(filters.params),
  })
  const { sortedRows, activeSort, onSort } = useSortableRows(query.data?.rows, cashierSortAccessors)

  if (query.isError) {
    return (
      <ErrorState
        message={query.error instanceof Error ? query.error.message : 'Could not load cashier performance.'}
        onRetry={() => query.refetch()}
      />
    )
  }

  if (query.isPending) {
    return (
      <Table>
        <Table.Head>
          <Table.HeaderCell>Cashier</Table.HeaderCell>
          <Table.HeaderCell align="right">Net sales</Table.HeaderCell>
          <Table.HeaderCell align="right">Gross sales</Table.HeaderCell>
          <Table.HeaderCell align="right">Transactions</Table.HeaderCell>
          <Table.HeaderCell align="right">Average sale</Table.HeaderCell>
          <Table.HeaderCell align="right">Discounts</Table.HeaderCell>
          <Table.HeaderCell align="right">Returns</Table.HeaderCell>
          <Table.HeaderCell align="right">Voids</Table.HeaderCell>
          <Table.HeaderCell align="right">Void approvals</Table.HeaderCell>
          <Table.HeaderCell align="right">Return approvals</Table.HeaderCell>
          <Table.HeaderCell align="right">Fulfillment cancel approvals</Table.HeaderCell>
        </Table.Head>
        <Table.Body>
          {Array.from({ length: 4 }).map((_, i) => (
            <Table.Row key={i}>
              {Array.from({ length: 11 }).map((__, j) => (
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
    return <EmptyState title="No cashier activity" description="No sales in this date range." />
  }

  return (
    <Table>
      <Table.Head>
        <Table.HeaderCell>Cashier</Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="netSales" activeSort={activeSort} onSort={onSort}>
          Net sales
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="grossSales" activeSort={activeSort} onSort={onSort}>
          Gross sales
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="transactions" activeSort={activeSort} onSort={onSort}>
          Transactions
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="averageSale" activeSort={activeSort} onSort={onSort}>
          Average sale
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="discounts" activeSort={activeSort} onSort={onSort}>
          Discounts
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="returnsValue" activeSort={activeSort} onSort={onSort}>
          Returns
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="voidedValue" activeSort={activeSort} onSort={onSort}>
          Voids
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="voidApprovalsCount" activeSort={activeSort} onSort={onSort}>
          Void approvals
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="returnApprovalsCount" activeSort={activeSort} onSort={onSort}>
          Return approvals
        </Table.HeaderCell>
        <Table.HeaderCell align="right" sortKey="fulfillmentCancelApprovalsCount" activeSort={activeSort} onSort={onSort}>
          Fulfillment cancel approvals
        </Table.HeaderCell>
      </Table.Head>
      <Table.Body>
        {sortedRows.map((r) => (
          <Table.Row key={r.cashierUserId}>
            <Table.Cell>
              <Link
                to={buildSalesDrilldownUrl({ fromUtc: query.data.fromUtc, toUtc: query.data.toUtc, cashierUserId: r.cashierUserId })}
                className="font-semibold text-primary-700 hover:underline"
              >
                {r.cashierName}
              </Link>
            </Table.Cell>
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
            <Table.Cell align="right">{r.voidApprovalsCount}</Table.Cell>
            <Table.Cell align="right">{r.returnApprovalsCount}</Table.Cell>
            <Table.Cell align="right">{r.fulfillmentCancelApprovalsCount}</Table.Cell>
          </Table.Row>
        ))}
      </Table.Body>
    </Table>
  )
}
