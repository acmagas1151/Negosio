import { useSearchParams } from 'react-router-dom'
import type { ReportFilterParams, ReportPeriod } from '../api/types'

const DEFAULT_PERIOD: ReportPeriod = 'Today'

export interface ReportFiltersState {
  period: ReportPeriod
  fromDate: string
  toDate: string
  branchId: string | undefined
  registerId: string | undefined
  cashierId: string | undefined
  /** The exact shape every report endpoint accepts — pass straight to reportsApi.*. */
  params: ReportFilterParams
  setPeriod: (period: ReportPeriod) => void
  setCustomRange: (fromDate: string, toDate: string) => void
  setBranchId: (branchId: string | undefined) => void
  setRegisterId: (registerId: string | undefined) => void
  setCashierId: (cashierId: string | undefined) => void
}

/** URL-synced report filters — shareable/bookmarkable links, survives a refresh, matches this
 * app's existing usePagedQuery convention for list filters. */
export function useReportFilters(): ReportFiltersState {
  const [search, setSearch] = useSearchParams()

  const period = (search.get('period') as ReportPeriod | null) ?? DEFAULT_PERIOD
  const fromDate = search.get('fromDate') ?? ''
  const toDate = search.get('toDate') ?? ''
  const branchId = search.get('branchId') ?? undefined
  const registerId = search.get('registerId') ?? undefined
  const cashierId = search.get('cashierId') ?? undefined

  const patch = (mutate: (next: URLSearchParams) => void) => {
    setSearch(
      (prev) => {
        const next = new URLSearchParams(prev)
        mutate(next)
        return next
      },
      { replace: true },
    )
  }

  return {
    period,
    fromDate,
    toDate,
    branchId,
    registerId,
    cashierId,
    params: {
      period,
      fromDate: period === 'Custom' ? fromDate || undefined : undefined,
      toDate: period === 'Custom' ? toDate || undefined : undefined,
      branchId,
      registerId,
      cashierId,
    },
    setPeriod: (next) =>
      patch((p) => {
        p.set('period', next)
        if (next !== 'Custom') {
          p.delete('fromDate')
          p.delete('toDate')
        }
      }),
    setCustomRange: (from, to) =>
      patch((p) => {
        p.set('period', 'Custom')
        p.set('fromDate', from)
        p.set('toDate', to)
      }),
    setBranchId: (next) =>
      patch((p) => {
        if (next) p.set('branchId', next)
        else p.delete('branchId')
        // A register only makes sense within a branch — clear it when the branch changes.
        p.delete('registerId')
      }),
    setRegisterId: (next) =>
      patch((p) => {
        if (next) p.set('registerId', next)
        else p.delete('registerId')
      }),
    setCashierId: (next) =>
      patch((p) => {
        if (next) p.set('cashierId', next)
        else p.delete('cashierId')
      }),
  }
}
