export type BusinessType = 'Retail' | 'FoodAndBeverage' | 'DiagnosticCenter'

export type UserRole =
  | 'Owner'
  | 'Admin'
  | 'Manager'
  | 'Cashier'
  | 'InventoryStaff'
  | 'KitchenStaff'
  | 'Viewer'

export interface RegisterBranchInput {
  name: string
  code: string
  addressLine1: string
  addressLine2: string | null
  city: string
  province: string
  postalCode: string | null
}

export interface RegisterOwnerInput {
  firstName: string
  lastName: string
  email: string
  password: string
}

export interface RegisterRequest {
  businessName: string
  businessType: BusinessType
  branch: RegisterBranchInput
  owner: RegisterOwnerInput
}

export interface RegisterResponse {
  tenantId: string
  branchId: string
  ownerUserId: string
}

export interface LoginRequest {
  email: string
  password: string
}

export interface AuthUser {
  id: string
  tenantId: string
  tenantName: string
  businessType: BusinessType
  firstName: string
  lastName: string
  email: string
  role: UserRole
}

export interface LoginResponse {
  accessToken: string
  expiresAtUtc: string
  user: AuthUser
}

export interface DashboardResponse {
  tenant: {
    id: string
    name: string
    businessType: BusinessType
  }
  branchCount: number
  userCount: number
}

export interface ApiErrorBody {
  code: string
  message: string
  traceId: string
  errors?: Record<string, string[]>
}
