import { useAuth } from '../auth/AuthContext'
import type { UserRole } from '../api/types'

type Capability = 'catalog:write' | 'costs:view'

// Mirrors src/Negosio.Application/Catalog/CatalogAccess.cs — keep in sync if the backend sets change.
//
// INVARIANT: `catalog:write` roles ⊆ `costs:view` roles. src/lib/catalogRequests.ts relies on this:
// its `costPrice ?? product.minCostPrice ?? 0` fallbacks assume a writer always sees the real
// (non-redacted) cost. If that ever changes, those fallbacks would silently write 0 over a
// redacted (null) cost — revisit them before loosening these sets.
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
