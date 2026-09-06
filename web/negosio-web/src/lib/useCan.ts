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
  | 'sales:return'
  | 'settings:write'
  | 'staff:manage'
  | 'branch:manage'
  | 'register:force-close'
  | 'sales:void'
  | 'staff:permissions'
  | 'register:cash-movement'

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
  // Mirrors SalesController's class-level RefundManage policy (Owner/Admin/Manager/Cashier) — the
  // Cashier direct-grant-vs-approval split is resolved server-side per return, not by this capability.
  'sales:return': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
  'settings:write': new Set<UserRole>(['Owner', 'Admin']),
  'staff:manage': new Set<UserRole>(['Owner', 'Admin']),
  'branch:manage': new Set<UserRole>(['Owner', 'Admin']),
  // Mirrors AuthorizationPolicies.RegisterForceClose — an administrative override, not RegisterManage.
  'register:force-close': new Set<UserRole>(['Owner', 'Admin']),
  // Mirrors SalesController's class-level SalesView policy (Owner/Admin/Manager/Cashier) — the
  // Cashier direct-vs-approval split is resolved server-side per sale, not by this capability.
  'sales:void': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
  // Mirrors StaffController's new StaffView/StaffPermissionManage overrides (Owner/Admin/Manager) —
  // distinct from 'staff:manage' (Owner/Admin only), which still gates every other staff action.
  'staff:permissions': new Set<UserRole>(['Owner', 'Admin', 'Manager']),
  // Mirrors RegisterSessionsController's class-level PosOperate policy — ownership of the specific
  // session (not just role) is enforced server-side by RegisterCashMovementService.
  'register:cash-movement': new Set<UserRole>(['Owner', 'Admin', 'Manager', 'Cashier']),
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
