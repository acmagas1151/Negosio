import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  AdjustInventoryRequest,
  InventoryListParams,
  InventoryRowDto,
  MovementListParams,
  PagedResult,
  StockMovementDto,
} from './types'

// Kept here for import-site compatibility; the implementation lives in ./branches.
export { branchesApi } from './branches'

export const inventoryApi = {
  list: (params: InventoryListParams) =>
    apiRequest<PagedResult<InventoryRowDto>>(`/api/inventory${qs({ ...params })}`),

  get: (id: string) => apiRequest<InventoryRowDto>(`/api/inventory/${id}`),

  adjust: (body: AdjustInventoryRequest) =>
    apiRequest<InventoryRowDto>('/api/inventory/adjustments', { method: 'POST', body }),

  movements: (params: MovementListParams) =>
    apiRequest<PagedResult<StockMovementDto>>(`/api/inventory/movements${qs({ ...params })}`),
}
