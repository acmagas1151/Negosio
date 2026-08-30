import { apiRequest } from './client'
import { qs } from './query-string'
import type {
  AdjustInventoryRequest,
  BranchDto,
  InventoryListParams,
  InventoryRowDto,
  MovementListParams,
  PagedResult,
  StockMovementDto,
} from './types'

export const branchesApi = {
  /** The current tenant's active branches — authoritative source for branch selectors. */
  list: () => apiRequest<BranchDto[]>('/api/branches'),
}

export const inventoryApi = {
  list: (params: InventoryListParams) =>
    apiRequest<PagedResult<InventoryRowDto>>(`/api/inventory${qs({ ...params })}`),

  get: (id: string) => apiRequest<InventoryRowDto>(`/api/inventory/${id}`),

  adjust: (body: AdjustInventoryRequest) =>
    apiRequest<InventoryRowDto>('/api/inventory/adjustments', { method: 'POST', body }),

  movements: (params: MovementListParams) =>
    apiRequest<PagedResult<StockMovementDto>>(`/api/inventory/movements${qs({ ...params })}`),
}
