import { useQuery } from '@tanstack/react-query'
import { settingsApi } from '../api/settings'

/** Shared tenant tax config. Read by the POS cart preview and the Settings page; both share this key. */
export const TAX_SETTINGS_QUERY_KEY = ['settings', 'tax'] as const

export function useTaxSettings() {
  return useQuery({
    queryKey: TAX_SETTINGS_QUERY_KEY,
    queryFn: settingsApi.getTax,
    staleTime: 5 * 60_000,
  })
}
