import { apiRequest } from './client'
import { qs } from './query-string'
import type { CategoryPerformanceDto, ReportFilterParams, ReportsOverviewDto, TopProductDto } from './types'

export const reportsApi = {
  overview: (params: ReportFilterParams) =>
    apiRequest<ReportsOverviewDto>(`/api/reports/overview${qs({ ...params })}`),

  topProducts: (params: ReportFilterParams, top: number) =>
    apiRequest<TopProductDto[]>(`/api/reports/top-products${qs({ ...params, top })}`),

  categories: (params: ReportFilterParams) =>
    apiRequest<CategoryPerformanceDto[]>(`/api/reports/categories${qs({ ...params })}`),
}
