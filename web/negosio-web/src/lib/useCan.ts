import { useMemo } from 'react'
import { useAuth } from '../auth/AuthContext'
import type { UserRole } from '../api/types'

export type Capability =
  | 'catalog:write'
  | 'inventory:write'
  | 'costs:view'
  | 'register:manage'
  | 'pos:operate'
  | 'sales:view'
  | 'refund:manage'
  | 'settings:write'
  | 'staff:manage'

// Mirrors src/Negosio.Application/Catalog/CatalogAccess.cs — keep in sync if the backend sets change.
// 'catalog:write'   -> CatalogWriterRoles       (CategoriesController / ProductsController write policies)
// 'inventory:write' -> InventoryWriterRoles     (InventoryController "adjustments" policy)
// 'costs:view'      -> CostReaderRoles          (service-level cost-price redaction)
//
// INVARIANT: `catalog:write` roles ⊆ `costs:view` roles. src/lib/catalogRequests.ts relies on this:
// its `costPrice ?? product.minCostPrice ?? 0` fallbacks assume a writer always sees the real
// (non-redacted) cost. If that ever changes, those fallbacks would silently write 0 over a
// redacted (null) cost — revisit them before loosening these sets.
const CAPABILITY_ROLES: Record<Capability, ReadonlySet<UserRole>> = {
  'catalog:write': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'inventory:write': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'InventoryStaff']),
  'costs:view': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'InventoryStaff']),
  // Mirrors src/Negosio.Api/Authorization/AuthorizationPolicies.cs (RegisterManage / PosOperate /
  // SalesView / RefundManage / TenantSettingsWrite). Keep in sync if the backend role sets change.
  'register:manage': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'pos:operate': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
  'sales:view': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
  'refund:manage': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  'settings:write': new Set<UserRole>(['Owner', 'Admin']),
  'staff:manage': new Set<UserRole>(['Owner', 'Admin']),
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

/** Returns a stable predicate — for callers (nav) that must check several capabilities at once. */
export function useCapabilities(): (capability: Capability) => boolean {
  const { user } = useAuth()
  return useMemo(() => {
    const role = user?.role
    return (capability: Capability) => (role ? CAPABILITY_ROLES[capability].has(role) : false)
  }, [user?.role])
}
