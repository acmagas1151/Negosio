import { apiRequest } from './client'
import type {
  CancelDeliveryRequest,
  CancelPickupRequest,
  CancellationResultDto,
  CreateDeliveryReceiptRequest,
  CreatePickupRequest,
  FulfillmentScheduleDto,
  SaleFulfillmentSummaryDto,
} from './types'

/** Whole-sale fulfillment: one active schedule per sale, covering every item at its full quantity.
 * See the DTO block in types.ts for the full backend-route contract. */
export const fulfillmentApi = {
  createDelivery: (saleId: string, body: CreateDeliveryReceiptRequest) =>
    apiRequest<FulfillmentScheduleDto>(`/api/sales/${saleId}/delivery-receipts`, { method: 'POST', body }),

  createPickup: (saleId: string, body: CreatePickupRequest) =>
    apiRequest<FulfillmentScheduleDto>(`/api/sales/${saleId}/pickups`, { method: 'POST', body }),

  listForSale: (saleId: string) =>
    apiRequest<FulfillmentScheduleDto[]>(`/api/sales/${saleId}/delivery-receipts`),

  getSaleSummary: (saleId: string) =>
    apiRequest<SaleFulfillmentSummaryDto>(`/api/sales/${saleId}/fulfillment`),

  get: (id: string) => apiRequest<FulfillmentScheduleDto>(`/api/delivery-receipts/${id}`),

  markDelivered: (id: string) =>
    apiRequest<FulfillmentScheduleDto>(`/api/delivery-receipts/${id}/deliver`, { method: 'POST' }),

  markClaimed: (id: string) =>
    apiRequest<FulfillmentScheduleDto>(`/api/delivery-receipts/${id}/claim`, { method: 'POST' }),

  cancelDelivery: (id: string, body: CancelDeliveryRequest) =>
    apiRequest<CancellationResultDto>(`/api/delivery-receipts/${id}/cancel`, { method: 'POST', body }),

  cancelPickup: (id: string, body: CancelPickupRequest) =>
    apiRequest<CancellationResultDto>(`/api/pickups/${id}/cancel`, { method: 'POST', body }),
}
