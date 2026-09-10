import { ApiError, apiRequest } from './client'
import type { CreateDeliveryReceiptRequest, DeliveryReceiptDto } from './types'

/** Persistent Delivery Receipt (Plan B) — one DR per sale, printed on A4. */
export const deliveryReceiptsApi = {
  /**
   * GET /api/sales/{id}/delivery-receipt.
   * 404 is returned both when no DR exists yet and when the sale id is unknown —
   * callers cannot tell the two apart, so both map to `null`.
   */
  getForSale: async (saleId: string): Promise<DeliveryReceiptDto | null> => {
    try {
      return await apiRequest<DeliveryReceiptDto>(`/api/sales/${saleId}/delivery-receipt`)
    } catch (err) {
      if (err instanceof ApiError && err.status === 404) return null
      throw err
    }
  },

  /** POST /api/sales/{id}/delivery-receipt — creates (201) or returns the existing DR (200). */
  createForSale: (saleId: string, body: CreateDeliveryReceiptRequest) =>
    apiRequest<DeliveryReceiptDto>(`/api/sales/${saleId}/delivery-receipt`, { method: 'POST', body }),

  /** GET /api/delivery-receipts/{id}. */
  get: (id: string) => apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}`),
}
