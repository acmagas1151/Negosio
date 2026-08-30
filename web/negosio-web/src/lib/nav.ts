import {
  BarChart3,
  ClipboardList,
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
    items: [
      { label: 'Inventory', icon: Warehouse, enabled: false },
      { label: 'POS', icon: ShoppingCart, enabled: false },
      { label: 'Sales', icon: ClipboardList, enabled: false },
      { label: 'Reports', icon: BarChart3, enabled: false },
      { label: 'Staff', icon: Users, enabled: false },
      { label: 'Settings', icon: Settings, enabled: false },
    ],
  },
]
