import {
  BarChart3,
  Building2,
  Calculator,
  ClipboardList,
  History,
  LayoutDashboard,
  Package,
  Settings,
  ShoppingCart,
  Tag,
  Users,
  Warehouse,
} from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { Capability } from './useCan'

export interface NavItem {
  label: string
  icon: LucideIcon
  /** Present only for routes that exist today. */
  to?: string
  enabled: boolean
  /**
   * If set, the item is only shown to a role that has this capability. Pages whose backend
   * read endpoints are open to everyone (Catalog, Inventory, Dashboard) are left ungated —
   * their write controls gate themselves via `useCan`. Pages gated here would otherwise only
   * lead to a 403.
   */
  capability?: Capability
}

export interface NavGroup {
  /** Optional uppercase section label rendered above the group. */
  label?: string
  items: NavItem[]
}

export const NAV_GROUPS: NavGroup[] = [
  {
    items: [{ label: 'Dashboard', icon: LayoutDashboard, to: '/dashboard', enabled: true }],
  },
  {
    label: 'Catalog',
    items: [
      { label: 'Products', icon: Package, to: '/products', enabled: true },
      { label: 'Categories', icon: Tag, to: '/categories', enabled: true },
    ],
  },
  {
    label: 'Inventory',
    items: [
      { label: 'Stock levels', icon: Warehouse, to: '/inventory', enabled: true },
      { label: 'Stock movements', icon: History, to: '/inventory/movements', enabled: true },
    ],
  },
  {
    label: 'Point of sale',
    items: [
      { label: 'POS', icon: ShoppingCart, to: '/pos', enabled: true, capability: 'pos:operate' },
      {
        // Register *management*. Cashiers choose a register from the POS flow, not here.
        label: 'Registers',
        icon: Calculator,
        to: '/registers',
        enabled: true,
        capability: 'register:manage',
      },
      { label: 'Sales', icon: ClipboardList, to: '/sales', enabled: true, capability: 'sales:view' },
    ],
  },
  {
    items: [
      // Placeholder — hidden from operational-only roles (Cashier) until Reporting ships.
      { label: 'Reports', icon: BarChart3, enabled: false, capability: 'register:manage' },
      { label: 'Staff', icon: Users, to: '/staff', enabled: true, capability: 'staff:manage' },
      { label: 'Branches', icon: Building2, to: '/branches', enabled: true, capability: 'branch:manage' },
      {
        label: 'Settings',
        icon: Settings,
        to: '/settings',
        enabled: true,
        capability: 'settings:write',
      },
    ],
  },
]
