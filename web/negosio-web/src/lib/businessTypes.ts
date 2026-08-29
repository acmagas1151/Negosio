import { Stethoscope, Store, UtensilsCrossed } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { BusinessType } from '../api/types'

export interface BusinessTypeOption {
  value: BusinessType
  title: string
  description: string
  available: boolean
  badge?: string
  /** Copy for the dashboard "Next Setup Step" card. */
  nextStep: string
  /** Visual identity for the selection card. */
  icon: LucideIcon
  /** Tailwind classes for the icon container (bg + text). */
  accentClass: string
}

export const businessTypeOptions: BusinessTypeOption[] = [
  {
    value: 'Retail',
    title: 'Retail',
    description: 'Retail stores, shops, mini-marts and similar businesses.',
    available: true,
    nextStep: 'Set up your product catalog',
    icon: Store,
    accentClass: 'bg-primary-50 text-primary-600',
  },
  {
    value: 'FoodAndBeverage',
    title: 'Food & Beverage',
    description: 'Restaurants, cafés, food shops and similar businesses.',
    available: true,
    nextStep: 'Set up your menu',
    icon: UtensilsCrossed,
    accentClass: 'bg-orange-light text-orange',
  },
  {
    value: 'DiagnosticCenter',
    title: 'Diagnostic Center',
    description: 'Laboratory and diagnostic operations.',
    available: false,
    badge: 'Coming Soon',
    nextStep: 'Set up your test directory',
    icon: Stethoscope,
    accentClass: 'bg-purple-light text-purple',
  },
]

export function businessTypeLabel(value: BusinessType): string {
  return businessTypeOptions.find((o) => o.value === value)?.title ?? value
}

export function nextSetupStep(value: BusinessType): string {
  return businessTypeOptions.find((o) => o.value === value)?.nextStep ?? 'Continue setting up your business'
}
