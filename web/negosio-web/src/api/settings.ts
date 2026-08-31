import { apiRequest } from './client'
import type { TaxSettingsDto, UpdateTaxSettingsRequest } from './types'

/** Tenant-wide settings. Tax is the only section today (GET open to any tenant user; PUT = TenantSettingsWrite). */
export const settingsApi = {
  getTax: () => apiRequest<TaxSettingsDto>('/api/settings/tax'),

  updateTax: (body: UpdateTaxSettingsRequest) =>
    apiRequest<TaxSettingsDto>('/api/settings/tax', { method: 'PUT', body }),
}
