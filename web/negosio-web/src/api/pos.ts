import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  CheckoutRequest,
  CreateRegisterRequest,
  CreateReturnRequest,
  CloseRegisterSessionRequest,
  OpenRegisterSessionRequest,
  PagedResult,
  PosCatalogItemDto,
  PosCatalogParams,
  ReceiptDto,
  RegisterDto,
  RegisterListParams,
  RegisterSessionDto,
  SaleDetailDto,
  SaleListParams,
  SaleResultDto,
  SaleReturnDto,
  SaleSummaryDto,
  UpdateRegisterRequest,
} from './types'

export const registersApi = {
  list: (params: RegisterListParams) =>
    apiRequest<PagedResult<RegisterDto>>(`/api/registers${qs({ ...params })}`),
  get: (id: string) => apiRequest<RegisterDto>(`/api/registers/${id}`),
  create: (body: CreateRegisterRequest) =>
    apiRequest<RegisterDto>('/api/registers', { method: 'POST', body }),
  update: (id: string, body: UpdateRegisterRequest) =>
    apiRequest<RegisterDto>(`/api/registers/${id}`, { method: 'PUT', body }),
  deactivate: (id: string) => apiRequest<void>(`/api/registers/${id}`, { method: 'DELETE' }),
}

export const sessionsApi = {
  open: (body: OpenRegisterSessionRequest) =>
    apiRequest<RegisterSessionDto>('/api/register-sessions/open', { method: 'POST', body }),
  current: (params: { registerId?: string; branchId?: string }) =>
    apiRequest<RegisterSessionDto>(`/api/register-sessions/current${qs({ ...params })}`),
  close: (id: string, body: CloseRegisterSessionRequest) =>
    apiRequest<RegisterSessionDto>(`/api/register-sessions/${id}/close`, { method: 'POST', body }),
}

export const posCatalogApi = {
  search: (params: PosCatalogParams) =>
    apiRequest<PagedResult<PosCatalogItemDto>>(`/api/pos/catalog${qs({ ...params })}`),
  barcode: (code: string, branchId: string) =>
    apiRequest<PosCatalogItemDto>(
      `/api/pos/catalog/barcode/${encodeURIComponent(code)}${qs({ branchId })}`,
    ),
}

export const checkoutApi = {
  checkout: (body: CheckoutRequest) =>
    apiRequest<SaleResultDto>('/api/pos/checkout', { method: 'POST', body }),
}

export const salesApi = {
  list: (params: SaleListParams) =>
    apiRequest<PagedResult<SaleSummaryDto>>(`/api/sales${qs({ ...params })}`),
  get: (id: string) => apiRequest<SaleDetailDto>(`/api/sales/${id}`),
  receipt: (id: string) => apiRequest<ReceiptDto>(`/api/sales/${id}/receipt`),
  listReturns: (id: string) => apiRequest<SaleReturnDto[]>(`/api/sales/${id}/returns`),
  createReturn: (id: string, body: CreateReturnRequest) =>
    apiRequest<SaleReturnDto>(`/api/sales/${id}/returns`, { method: 'POST', body }),
}
