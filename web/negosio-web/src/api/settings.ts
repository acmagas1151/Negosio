import { apiRequest } from './client'
import type {
  BusinessInfoDto,
  TaxSettingsDto,
  UpdateBusinessInfoRequest,
  UpdateTaxSettingsRequest,
} from './types'

/** Tenant-wide settings. Tax is the only section today (GET open to any tenant user; PUT = TenantSettingsWrite). */
export const settingsApi = {
  getTax: () => apiRequest<TaxSettingsDto>('/api/settings/tax'),

  updateTax: (body: UpdateTaxSettingsRequest) =>
    apiRequest<TaxSettingsDto>('/api/settings/tax', { method: 'PUT', body }),

  // Business identity shown on receipts. GET open to any tenant user; PUT = Owner/Admin.
  getBusinessInfo: () => apiRequest<BusinessInfoDto>('/api/settings/business-info'),

  updateBusinessInfo: (body: UpdateBusinessInfoRequest) =>
    apiRequest<BusinessInfoDto>('/api/settings/business-info', { method: 'PUT', body }),
}
