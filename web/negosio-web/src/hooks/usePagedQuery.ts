import { useCallback, useEffect, useMemo, useState } from 'react'
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
    patch((next) => {
      if (search) next.set('q', search)
      else next.delete('q')
      next.delete('page')
    })
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search])

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
    sortBy,
    sortDirection,
    setSort,
    setPage,
  }
}
