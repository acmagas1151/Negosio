import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  CategoryPerformanceDto,
  DeliveryFulfillmentReportParams,
  DeliveryFulfillmentReportResultDto,
  DeliveryReportParams,
  DeliveryReportResultDto,
  FulfillmentReportParams,
  FulfillmentReportResultDto,
  PickupReportParams,
  PickupReportResultDto,
  ReportFilterParams,
  ReportsOverviewDto,
  TopProductDto,
} from './types'

export const reportsApi = {
  overview: (params: ReportFilterParams) =>
    apiRequest<ReportsOverviewDto>(`/api/reports/overview${qs({ ...params })}`),

  topProducts: (params: ReportFilterParams, top: number) =>
    apiRequest<TopProductDto[]>(`/api/reports/top-products${qs({ ...params, top })}`),

  categories: (params: ReportFilterParams) =>
    apiRequest<CategoryPerformanceDto[]>(`/api/reports/categories${qs({ ...params })}`),

  deliveries: (params: DeliveryReportParams) =>
    apiRequest<DeliveryReportResultDto>(`/api/reports/deliveries${qs({ ...params })}`),

  deliveryFulfillment: (params: DeliveryFulfillmentReportParams) =>
    apiRequest<DeliveryFulfillmentReportResultDto>(
      `/api/reports/delivery-fulfillment${qs({ ...params })}`,
    ),

  pickups: (params: PickupReportParams) =>
    apiRequest<PickupReportResultDto>(`/api/reports/pickup${qs({ ...params })}`),

  fulfillment: (params: FulfillmentReportParams) =>
    apiRequest<FulfillmentReportResultDto>(`/api/reports/fulfillment${qs({ ...params })}`),
}
