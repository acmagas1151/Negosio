import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useDebouncedValue } from './useDebouncedValue'

export interface PagedQueryState<F extends Record<string, string | undefined>> {
  page: number
  pageSize: number
  search: string
  searchInput: string
  setSearchInput: (v: string) => void
  filters: F
  setFilter: (key: keyof F, value: string | undefined) => void
  // Sets several filter keys in a single URL update. NOT equivalent to calling `setFilter`
  // repeatedly in the same handler: `setFilter` (like react-router's `setSearchParams`) resolves
  // its "previous params" from this hook's last-rendered `location.search`, not from any earlier
  // `setFilter` call made earlier in the same synchronous handler — so two sequential `setFilter`
  // calls each mutate from the SAME stale snapshot and the second call's `navigate` clobbers the
  // first's. Use `setFilters` whenever a single user action must change more than one filter key
  // atomically (e.g. clearing a drill-down's exact fromUtc/toUtc when the user edits the plain
  // date picker that drill-down had overridden).
  setFilters: (patch: Partial<F>) => void
  sortBy: string | undefined
  sortDirection: 'asc' | 'desc' | undefined
  setSort: (column: string) => void
  setPage: (page: number) => void
}

export function usePagedQuery<F extends Record<string, string | undefined>>(opts: {
  defaultFilters: F
  defaultPageSize?: number
  debounceMs?: number
}): PagedQueryState<F> {
  const { defaultFilters, defaultPageSize = 20, debounceMs = 350 } = opts
  const [params, setParams] = useSearchParams()

  const pageSize = defaultPageSize
  const page = Math.max(1, Number(params.get('page') ?? '1') || 1)
  const sortBy = params.get('sort') ?? undefined
  const sortDirection = (params.get('dir') as 'asc' | 'desc' | null) ?? undefined

  const filterKeys = useMemo(() => Object.keys(defaultFilters), [defaultFilters])
  const filters = useMemo(() => {
    const out = {} as F
    for (const key of filterKeys) {
      ;(out as Record<string, string | undefined>)[key] = params.get(key) ?? undefined
    }
    return out
  }, [params, filterKeys])

  const [searchInput, setSearchInput] = useState(params.get('q') ?? '')
  const search = useDebouncedValue(searchInput, debounceMs)

  // The last `q` value this hook itself pushed into the URL — so the resync effect below can tell
  // an external URL change (browser Back/Forward, a shared link) apart from its own write.
  const lastPushedRef = useRef(params.get('q') ?? '')

  // Mutate the URL immutably; always drop `page` on any filter/search/sort change.
  const patch = useCallback(
    (mutate: (next: URLSearchParams) => void) => {
      setParams(
        (prev) => {
          const next = new URLSearchParams(prev)
          mutate(next)
          return next
        },
        { replace: false },
      )
    },
    [setParams],
  )

  // Push the debounced search term into the URL (and reset page) when it settles.
  useEffect(() => {
    const current = params.get('q') ?? ''
    if (current === search) return
    lastPushedRef.current = search
    patch((next) => {
      if (search) next.set('q', search)
      else next.delete('q')
      next.delete('page')
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search])

  // Resync FROM the URL when `q` changes to a value this hook did not push (browser Back/Forward,
  // an externally-set link). Without this the input keeps showing — and querying — the stale term.
  useEffect(() => {
    const urlQ = params.get('q') ?? ''
    if (urlQ !== lastPushedRef.current && urlQ !== searchInput) {
      lastPushedRef.current = urlQ
      // oxlint-disable-next-line set-state-in-effect
      setSearchInput(urlQ)
    }
  }, [params, searchInput])

  const setFilter = useCallback(
    (key: keyof F, value: string | undefined) => {
      patch((next) => {
        if (value) next.set(String(key), value)
        else next.delete(String(key))
        next.delete('page')
      })
    },
    [patch],
  )

  const setFilters = useCallback(
    (filterPatch: Partial<F>) => {
      patch((next) => {
        for (const key of Object.keys(filterPatch)) {
          const value = filterPatch[key as keyof F]
          if (value) next.set(key, value)
          else next.delete(key)
        }
        next.delete('page')
      })
    },
    [patch],
  )

  const setSort = useCallback(
    (column: string) => {
      patch((next) => {
        const by = next.get('sort')
        const dir = next.get('dir')
        if (by !== column) {
          next.set('sort', column)
          next.set('dir', 'asc')
        } else if (dir === 'asc') {
          next.set('dir', 'desc')
        } else {
          next.delete('sort')
          next.delete('dir')
        }
        next.delete('page')
      })
    },
    [patch],
  )

  const setPage = useCallback(
    (n: number) => {
      patch((next) => {
        if (n <= 1) next.delete('page')
        else next.set('page', String(n))
      })
    },
    [patch],
  )

  return {
    page,
    pageSize,
    search,
    searchInput,
    setSearchInput,
    filters,
    setFilter,
    setFilters,
    sortBy,
    sortDirection,
    setSort,
    setPage,
  }
}
