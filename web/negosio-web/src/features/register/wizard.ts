import type { BusinessType, RegisterRequest } from '../../api/types'

export interface WizardData {
  businessType: BusinessType | null
  businessName: string
  branch: {
    name: string
    code: string
    addressLine1: string
    addressLine2: string
    city: string
    province: string
    postalCode: string
  }
  owner: {
    firstName: string
    lastName: string
    email: string
    password: string
    confirmPassword: string
  }
}

export const emptyWizardData: WizardData = {
  businessType: null,
  businessName: '',
  branch: { name: '', code: '', addressLine1: '', addressLine2: '', city: '', province: '', postalCode: '' },
  owner: { firstName: '', lastName: '', email: '', password: '', confirmPassword: '' },
}

export const STEP_TITLES = [
  'Choose Business Type',
  'Business Information',
  'First Branch',
  'Owner Account',
  'Review',
] as const

export type StepErrors = Record<string, string>

const PASSWORD_RULE = /^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z0-9]).{8,}$/

/** Client-side checks per step. The backend remains the source of truth. */
export function validateStep(step: number, data: WizardData): StepErrors {
  const errors: StepErrors = {}

  if (step === 0 && !data.businessType) {
    errors['businessType'] = 'Select a business type to continue.'
  }

  if (step === 1 && !data.businessName.trim()) {
    errors['businessName'] = 'Business name is required.'
  }

  if (step === 2) {
    if (!data.branch.name.trim()) errors['branch.name'] = 'Branch name is required.'
    if (!data.branch.code.trim()) errors['branch.code'] = 'Branch code is required.'
    if (!data.branch.addressLine1.trim()) errors['branch.addressLine1'] = 'Address line 1 is required.'
    if (!data.branch.city.trim()) errors['branch.city'] = 'City is required.'
    if (!data.branch.province.trim()) errors['branch.province'] = 'Province is required.'
  }

  if (step === 3) {
    if (!data.owner.firstName.trim()) errors['owner.firstName'] = 'First name is required.'
    if (!data.owner.lastName.trim()) errors['owner.lastName'] = 'Last name is required.'
    if (!/^\S+@\S+\.\S+$/.test(data.owner.email)) errors['owner.email'] = 'Enter a valid email address.'
    if (!PASSWORD_RULE.test(data.owner.password)) {
      errors['owner.password'] =
        'Password needs 8+ characters with an uppercase letter, a lowercase letter, a digit and a special character.'
    }
    if (data.owner.password !== data.owner.confirmPassword) {
      errors['owner.confirmPassword'] = 'Passwords do not match.'
    }
  }

  return errors
}

export function firstStepWithError(errorKeys: string[]): number | null {
  const stepOf: Record<string, number> = {
    businessType: 0,
    businessName: 1,
  }
  for (const key of errorKeys) {
    if (key in stepOf) return stepOf[key]
    if (key.startsWith('branch.')) return 2
    if (key.startsWith('owner.')) return 3
  }
  return null
}

export function toRegisterRequest(data: WizardData): RegisterRequest {
  return {
    businessName: data.businessName.trim(),
    businessType: data.businessType as BusinessType,
    branch: {
      name: data.branch.name.trim(),
      code: data.branch.code.trim(),
      addressLine1: data.branch.addressLine1.trim(),
      addressLine2: data.branch.addressLine2.trim() || null,
      city: data.branch.city.trim(),
      province: data.branch.province.trim(),
      postalCode: data.branch.postalCode.trim() || null,
    },
    owner: {
      firstName: data.owner.firstName.trim(),
      lastName: data.owner.lastName.trim(),
      email: data.owner.email.trim(),
      password: data.owner.password,
    },
  }
}
