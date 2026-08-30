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
  tenant: { id: string; name: string; businessType: BusinessType }
  branchCount: number
  userCount: number
  totalProducts: number
  activeCategories: number
  lowStockItems: number
  todaysSales: number
  todaysTransactions: number
  averageTransactionValue: number
}

export interface ApiErrorBody {
  code: string
  message: string
  traceId: string
  errors?: Record<string, string[]>
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface CategoryDto {
  id: string
  name: string
  description: string | null
  isActive: boolean
  productCount: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface CreateCategoryRequest {
  name: string
  description: string | null
}

export interface UpdateCategoryRequest {
  name: string
  description: string | null
  isActive: boolean
}

export interface CategoryListParams {
  search?: string
  isActive?: boolean
  page?: number
  pageSize?: number
}

export interface ProductVariantDto {
  id: string
  name: string
  isDefault: boolean
  sku: string | null
  barcode: string | null
  costPrice: number | null
  sellingPrice: number
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export interface ProductDto {
  id: string
  categoryId: string
  categoryName: string
  name: string
  description: string | null
  trackInventory: boolean
  isActive: boolean
  hasVariants: boolean
  variantCount: number
  sku: string | null
  barcode: string | null
  minCostPrice: number | null
  maxCostPrice: number | null
  minSellingPrice: number
  maxSellingPrice: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface ProductDetailDto {
  product: ProductDto
  variants: ProductVariantDto[]
}

export interface VariantInput {
  name: string
  sku: string | null
  barcode: string | null
  costPrice: number
  sellingPrice: number
}

export interface CreateProductRequest {
  categoryId: string
  name: string
  description: string | null
  trackInventory: boolean
  sku: string | null
  barcode: string | null
  costPrice: number
  sellingPrice: number
  variants: VariantInput[] | null
}

export interface UpdateProductRequest {
  categoryId: string
  name: string
  description: string | null
  trackInventory: boolean
  isActive: boolean
  sku: string | null
  barcode: string | null
  costPrice: number
  sellingPrice: number
}

export type ProductSortBy = 'name' | 'sellingprice' | 'createdatutc'

export interface ProductListParams {
  search?: string
  categoryId?: string
  isActive?: boolean
  trackInventory?: boolean
  page?: number
  pageSize?: number
  sortBy?: ProductSortBy
  sortDirection?: 'asc' | 'desc'
}
