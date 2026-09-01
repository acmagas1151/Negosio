import type { UserRole } from '../api/types'

export const ROLE_LABELS: Record<UserRole, string> = {
  Owner: 'Owner',
  Admin: 'Admin',
  Manager: 'Manager',
  Cashier: 'Cashier',
  InventoryStaff: 'Inventory staff',
  KitchenStaff: 'Kitchen staff',
  Viewer: 'Viewer',
}

export function roleLabel(role: UserRole): string {
  return ROLE_LABELS[role] ?? role
}

const NON_OWNER_ROLES: UserRole[] = [
  'Admin',
  'Manager',
  'Cashier',
  'InventoryStaff',
  'KitchenStaff',
  'Viewer',
]

/**
 * Roles the acting user may assign — mirrors StaffRoles.AssignableBy on the backend
 * (Owner grants any non-Owner role; Admin grants any role below Admin). The backend re-checks.
 */
export function assignableRoles(actor: UserRole): UserRole[] {
  if (actor === 'Owner') return NON_OWNER_ROLES
  if (actor === 'Admin') return NON_OWNER_ROLES.filter((r) => r !== 'Admin')
  return []
}
