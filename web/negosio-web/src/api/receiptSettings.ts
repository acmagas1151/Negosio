import { apiRequest } from './client'
import type { ReceiptSettingsDto, UpdateReceiptSettingsRequest } from './types'

/** Receipt settings — tenant-wide defaults and per-branch overrides. */
export const receiptSettingsApi = {
  get: (branchId?: string) => {
    const url = branchId !== undefined ? `/api/settings/receipts?branchId=${branchId}` : '/api/settings/receipts'
    return apiRequest<ReceiptSettingsDto>(url)
  },

  update: (branchId: string | undefined, body: UpdateReceiptSettingsRequest) => {
    const url = branchId !== undefined ? `/api/settings/receipts?branchId=${branchId}` : '/api/settings/receipts'
    return apiRequest<ReceiptSettingsDto>(url, { method: 'PUT', body })
  },

  reset: (branchId: string) => {
    const url = `/api/settings/receipts?branchId=${branchId}`
    return apiRequest<void>(url, { method: 'DELETE' })
  },
}
