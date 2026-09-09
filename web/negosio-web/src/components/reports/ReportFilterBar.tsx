import { useQuery } from '@tanstack/react-query'
import { branchesApi } from '../../api/branches'
import { registersApi } from '../../api/pos'
import { staffApi } from '../../api/staff'
import type { ReportPeriod } from '../../api/types'
import type { ReportFiltersState } from '../../hooks/useReportFilters'
import { Select } from '../ui'

const PERIOD_OPTIONS: { value: ReportPeriod; label: string }[] = [
  { value: 'Today', label: 'Today' },
  { value: 'Yesterday', label: 'Yesterday' },
  { value: 'Last7Days', label: 'Last 7 days' },
  { value: 'Last30Days', label: 'Last 30 days' },
  { value: 'ThisMonth', label: 'This month' },
  { value: 'Custom', label: 'Custom range' },
]

interface Props {
  filters: ReportFiltersState
  /** Only Owner/Admin see a branch picker at all — a Manager's data is already forced server-side,
   * and showing them a picker that silently does nothing would be misleading. */
  showBranchFilter: boolean
}

export function ReportFilterBar({ filters, showBranchFilter }: Props) {
  const branchesQuery = useQuery({
    queryKey: ['branches', 'reports-filter'],
    queryFn: () => branchesApi.list(),
    enabled: showBranchFilter,
  })
  const branches = branchesQuery.data ?? []

  const registersQuery = useQuery({
    queryKey: ['registers', 'reports-filter', filters.branchId],
    queryFn: () => registersApi.list({ branchId: filters.branchId, isActive: true, pageSize: 100 }),
  })
  const registers = registersQuery.data?.items ?? []

  const staffQuery = useQuery({ queryKey: ['staff', 'reports-filter'], queryFn: staffApi.list })
  const cashiers = (staffQuery.data ?? []).filter((m) => m.kind === 'Member' && m.status === 'Active')

  const customRangeReady = filters.period !== 'Custom' || (filters.fromDate && filters.toDate)

  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center">
      <Select
        aria-label="Date range"
        className="sm:w-44"
        value={filters.period}
        onChange={(e) => filters.setPeriod(e.target.value as ReportPeriod)}
      >
        {PERIOD_OPTIONS.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </Select>

      {filters.period === 'Custom' && (
        <div className="flex items-center gap-2">
          <input
            type="date"
            aria-label="From date"
            value={filters.fromDate}
            max={filters.toDate || undefined}
            onChange={(e) => filters.setCustomRange(e.target.value, filters.toDate)}
            className="h-9 rounded-lg border border-border-strong bg-white px-2.5 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
          <span className="text-text-muted">–</span>
          <input
            type="date"
            aria-label="To date"
            value={filters.toDate}
            min={filters.fromDate || undefined}
            onChange={(e) => filters.setCustomRange(filters.fromDate, e.target.value)}
            className="h-9 rounded-lg border border-border-strong bg-white px-2.5 text-sm text-text-primary focus:border-primary-500 focus:outline-none focus:ring-[3px] focus:ring-primary-500/15"
          />
        </div>
      )}

      {showBranchFilter && (
        <Select
          aria-label="Branch"
          className="sm:w-44"
          value={filters.branchId ?? ''}
          onChange={(e) => filters.setBranchId(e.target.value || undefined)}
        >
          <option value="">All branches</option>
          {branches.map((b) => (
            <option key={b.id} value={b.id}>
              {b.name}
            </option>
          ))}
        </Select>
      )}

      <Select
        aria-label="Register"
        className="sm:w-44"
        value={filters.registerId ?? ''}
        onChange={(e) => filters.setRegisterId(e.target.value || undefined)}
      >
        <option value="">All registers</option>
        {registers.map((r) => (
          <option key={r.id} value={r.id}>
            {r.name}
          </option>
        ))}
      </Select>

      <Select
        aria-label="Cashier"
        className="sm:w-44"
        value={filters.cashierId ?? ''}
        onChange={(e) => filters.setCashierId(e.target.value || undefined)}
      >
        <option value="">All cashiers</option>
        {cashiers.map((c) => (
          <option key={c.id} value={c.id}>
            {`${c.firstName ?? ''} ${c.lastName ?? ''}`.trim() || c.email}
          </option>
        ))}
      </Select>

      {!customRangeReady && (
        <span className="text-[13px] text-text-muted">Pick both a start and end date.</span>
      )}
    </div>
  )
}
