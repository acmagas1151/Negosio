import {
  BarChart3,
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

export interface NavItem {
  label: string
  icon: LucideIcon
  /** Present only for routes that exist today. */
  to?: string
  enabled: boolean
}

export interface NavGroup {
  /** Optional uppercase section label rendered above the group. */
  label?: string
  items: NavItem[]
}

// TODO(staff-management): nav items are not yet gated by `useCan` — every entry shows for every
// role. This is inert today (only Owner accounts are reachable; no staff/invite flow exists).
// When Staff Management lands, gate items by capability (e.g. POS → 'pos:operate',
// Sales → 'sales:view', Registers → 'register:manage') so non-owner roles don't see or hit
// endpoints they can't use. Tracked in the project status doc (§10 "role-based nav gating").

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
      { label: 'POS', icon: ShoppingCart, to: '/pos', enabled: true },
      { label: 'Registers', icon: Calculator, to: '/registers', enabled: true },
      { label: 'Sales', icon: ClipboardList, to: '/sales', enabled: true },
    ],
  },
  {
    items: [
      { label: 'Reports', icon: BarChart3, enabled: false },
      { label: 'Staff', icon: Users, enabled: false },
      { label: 'Settings', icon: Settings, enabled: false },
    ],
  },
]
