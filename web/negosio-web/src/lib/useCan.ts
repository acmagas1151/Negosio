import { useAuth } from '../auth/AuthContext'
import type { UserRole } from '../api/types'

type Capability = 'catalog:write' | 'costs:view'

// Mirrors src/Negosio.Application/Catalog/CatalogAccess.cs — keep in sync if the backend sets change.
const CAPABILITY_ROLES: Record<Capability, ReadonlySet<UserRole>> = {
  'catalog:write': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'costs:view': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'InventoryStaff']),
}

/**
 * UX affordance only — the backend independently enforces every rule and redacts cost fields.
 * Never gate a cost *value* on this; null-check the API field instead.
 */
export function useCan(capability: Capability): boolean {
  const { user } = useAuth()
  if (!user) return false
  return CAPABILITY_ROLES[capability].has(user.role)
}
