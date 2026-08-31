import { useQuery } from '@tanstack/react-query'
import { taxSettingsApi } from '../api/pos'

/** Read-only feed of the tenant's tax config, used only for the POS cart's preview totals. */
export function useTaxSettings() {
  return useQuery({
    queryKey: ['settings', 'tax'],
    queryFn: taxSettingsApi.get,
    staleTime: 5 * 60_000,
  })
}
