import { apiRequest } from './client'
import { qs } from './query-string'
import type { BranchDto, CreateBranchRequest, UpdateBranchRequest } from './types'

/** Branch selectors + Branch Management. The backend scopes the list to the caller's branch. */
export const branchesApi = {
  list: (params: { includeInactive?: boolean } = {}) =>
    apiRequest<BranchDto[]>(`/api/branches${qs({ includeInactive: params.includeInactive })}`),

  get: (id: string) => apiRequest<BranchDto>(`/api/branches/${id}`),

  create: (body: CreateBranchRequest) =>
    apiRequest<BranchDto>('/api/branches', { method: 'POST', body }),

  update: (id: string, body: UpdateBranchRequest) =>
    apiRequest<BranchDto>(`/api/branches/${id}`, { method: 'PUT', body }),

  deactivate: (id: string) =>
    apiRequest<BranchDto>(`/api/branches/${id}/deactivate`, { method: 'POST' }),

  reactivate: (id: string) =>
    apiRequest<BranchDto>(`/api/branches/${id}/reactivate`, { method: 'POST' }),
}
