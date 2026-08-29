import {
  BarChart3,
  LayoutDashboard,
  Package,
  Settings,
  ShoppingCart,
  Users,
  Warehouse,
  ClipboardList,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'

export interface NavItem {
  label: string
  icon: LucideIcon
  /** Present only for routes that exist today. */
  to?: string
  enabled: boolean
}

/**
 * Full target navigation. Only "Dashboard" is wired to a route in Phase 1;
 * the rest render as disabled "Soon" items so the shell matches the product
 * shape without pretending features exist.
 */
export const NAV_ITEMS: NavItem[] = [
  { label: 'Dashboard', icon: LayoutDashboard, to: '/dashboard', enabled: true },
  { label: 'POS', icon: ShoppingCart, enabled: false },
  { label: 'Products', icon: Package, enabled: false },
  { label: 'Inventory', icon: Warehouse, enabled: false },
  { label: 'Orders', icon: ClipboardList, enabled: false },
  { label: 'Reports', icon: BarChart3, enabled: false },
  { label: 'Staff', icon: Users, enabled: false },
  { label: 'Settings', icon: Settings, enabled: false },
]
