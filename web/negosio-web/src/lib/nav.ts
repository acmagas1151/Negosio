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
