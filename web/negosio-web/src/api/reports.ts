import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  BranchPerformanceResultDto,
  CashierPerformanceResultDto,
  CategoryPerformanceDto,
  DeliveryFulfillmentReportParams,
  DeliveryFulfillmentReportResultDto,
  DeliveryReportParams,
  DeliveryReportResultDto,
  PagedResult,
  PickupReportParams,
  PickupReportResultDto,
  RegisterPerformanceResultDto,
  RegisterSessionReconciliationRowDto,
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

  branchPerformance: (params: ReportFilterParams) =>
    apiRequest<BranchPerformanceResultDto>(`/api/reports/branch-performance${qs({ ...params })}`),

  registerPerformance: (params: ReportFilterParams) =>
    apiRequest<RegisterPerformanceResultDto>(`/api/reports/register-performance${qs({ ...params })}`),

  registerSessions: (params: ReportFilterParams & { page: number; pageSize: number }) =>
    apiRequest<PagedResult<RegisterSessionReconciliationRowDto>>(
      `/api/reports/register-sessions${qs({ ...params })}`,
    ),

  cashierPerformance: (params: ReportFilterParams) =>
    apiRequest<CashierPerformanceResultDto>(`/api/reports/cashier-performance${qs({ ...params })}`),
}
