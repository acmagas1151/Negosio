import { useQuery } from '@tanstack/react-query'
import { receiptSettingsApi } from '../api/receiptSettings'

/** Receipt settings — tenant-wide defaults (branchId undefined) and per-branch overrides (branchId set). */
export const RECEIPT_SETTINGS_QUERY_KEY = (branchId?: string) =>
  ['settings', 'receipts', branchId ?? 'tenant'] as const

export function useReceiptSettings(branchId?: string) {
  return useQuery({
    queryKey: RECEIPT_SETTINGS_QUERY_KEY(branchId),
    queryFn: () => receiptSettingsApi.get(branchId),
    staleTime: 5 * 60_000,
  })
}
