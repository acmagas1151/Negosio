import { apiRequest } from './client'
import type {
  CancelDeliveryReceiptRequest,
  CreateDeliveryReceiptBatchRequest,
  CreateDeliveryReceiptRequest,
  DeliveryReceiptBatchResultDto,
  DeliveryReceiptDto,
  SaleDeliverySummaryDto,
} from './types'

/** Scheduled, partial, multi-delivery fulfillment. See the DTO block in types.ts for the full
 * backend-route contract. */
export const deliveryReceiptsApi = {
  createBatch: (saleId: string, body: CreateDeliveryReceiptBatchRequest) =>
    apiRequest<DeliveryReceiptBatchResultDto>(`/api/sales/${saleId}/delivery-receipts/batch`, {
      method: 'POST',
      body,
    }),

  create: (saleId: string, body: CreateDeliveryReceiptRequest) =>
    apiRequest<DeliveryReceiptDto>(`/api/sales/${saleId}/delivery-receipts`, { method: 'POST', body }),

  listForSale: (saleId: string) =>
    apiRequest<DeliveryReceiptDto[]>(`/api/sales/${saleId}/delivery-receipts`),

  getSaleSummary: (saleId: string) =>
    apiRequest<SaleDeliverySummaryDto>(`/api/sales/${saleId}/delivery-summary`),

  get: (id: string) => apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}`),

  markDelivered: (id: string) =>
    apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}/deliver`, { method: 'POST' }),

  cancel: (id: string, body: CancelDeliveryReceiptRequest) =>
    apiRequest<DeliveryReceiptDto>(`/api/delivery-receipts/${id}/cancel`, { method: 'POST', body }),
}
