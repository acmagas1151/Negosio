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

// ---- Branches (read-only selector feed) ----

export interface BranchDto {
  id: string
  name: string
  code: string
  isActive: boolean
}

// ---- Inventory ----

export type InventoryStatus = 'OutOfStock' | 'LowStock' | 'InStock'

export interface InventoryRowDto {
  id: string
  branchId: string
  branchName: string
  productId: string
  productName: string
  productVariantId: string
  variantName: string
  isDefaultVariant: boolean
  sku: string | null
  quantityOnHand: number
  reorderLevel: number
  status: InventoryStatus
  costPrice: number | null
  sellingPrice: number
  concurrencyToken: string
  updatedAtUtc: string
}

export type StockMovementType =
  | 'OpeningStock'
  | 'AdjustmentIncrease'
  | 'AdjustmentDecrease'
  | 'Sale'
  | 'Return'
  | 'TransferIn'
  | 'TransferOut'
  | 'Purchase'
  | 'Waste'

export interface StockMovementDto {
  id: string
  branchId: string
  branchName: string
  productId: string
  productName: string
  productVariantId: string
  variantName: string
  type: StockMovementType
  quantity: number
  quantityBefore: number
  quantityAfter: number
  reason: string | null
  createdByUserId: string
  createdByName: string
  createdAtUtc: string
}

export interface AdjustInventoryRequest {
  branchId: string
  productId: string
  productVariantId: string | null
  adjustment: number
  reason: string
  reorderLevel: number | null
  expectedConcurrencyToken: string | null
}

export interface InventoryListParams {
  branchId?: string
  productId?: string
  categoryId?: string
  search?: string
  lowStock?: boolean
  page?: number
  pageSize?: number
}

export interface MovementListParams {
  branchId?: string
  productId?: string
  productVariantId?: string
  type?: StockMovementType
  fromUtc?: string
  toUtc?: string
  page?: number
  pageSize?: number
}

// ---- POS: registers & sessions ----

export type RegisterSessionStatus = 'Open' | 'Closed'

export interface RegisterDto {
  id: string
  branchId: string
  branchName: string
  name: string
  code: string
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
}

export interface CreateRegisterRequest {
  branchId: string
  name: string
  code: string
}

export interface UpdateRegisterRequest {
  name: string
  code: string
  isActive: boolean
}

export interface RegisterListParams {
  branchId?: string
  isActive?: boolean
  page?: number
  pageSize?: number
}

export interface RegisterSessionDto {
  id: string
  branchId: string
  registerId: string
  registerName: string
  status: RegisterSessionStatus
  openedByUserId: string
  openedByName: string
  openedAtUtc: string
  closedAtUtc: string | null
  openingCash: number
  closingCash: number | null
  expectedCash: number | null
  cashDifference: number | null
}

export interface OpenRegisterSessionRequest {
  registerId: string
  openingCash: number
}

export interface CloseRegisterSessionRequest {
  closingCash: number
}

// ---- POS: catalog & checkout ----

export type PaymentMethod = 'Cash' | 'Card' | 'GCash' | 'Maya' | 'BankTransfer' | 'Other'
export type DiscountType = 'None' | 'FixedAmount' | 'Percentage'

export interface PosCatalogItemDto {
  productId: string
  productVariantId: string
  productName: string
  variantName: string | null
  sku: string | null
  barcode: string | null
  sellingPrice: number
  quantityAvailable: number
  trackInventory: boolean
  isAvailable: boolean
}

export interface PosCatalogParams {
  branchId: string
  search?: string
  page?: number
  pageSize?: number
}

export interface CheckoutDiscountInput {
  type: DiscountType
  value: number
}

export interface CheckoutItemInput {
  productVariantId: string
  quantity: number
  discount: CheckoutDiscountInput | null
}

export interface CheckoutPaymentInput {
  method: PaymentMethod
  receivedAmount?: number | null
  amount?: number | null
  referenceNumber?: string | null
}

export interface CheckoutRequest {
  branchId: string
  registerSessionId: string
  clientRequestId: string
  items: CheckoutItemInput[]
  payments: CheckoutPaymentInput[]
}

export interface SaleResultDto {
  saleId: string
  saleNumber: string
  status: SaleStatus
  subtotal: number
  discountTotal: number
  taxTotal: number
  grandTotal: number
  amountPaid: number
  changeDue: number
  wasExistingRequest: boolean
}

// ---- Sales history & detail ----

export type SaleStatus = 'Completed' | 'Voided' | 'Refunded' | 'PartiallyRefunded'

export interface SaleItemDto {
  id: string
  productVariantId: string
  productName: string
  variantName: string | null
  sku: string | null
  barcode: string | null
  unitPrice: number
  quantity: number
  grossAmount: number
  discountAmount: number
  taxAmount: number
  netAmount: number
  costPriceSnapshot: number | null
  returnedQuantity: number
}

export interface SalePaymentDto {
  id: string
  method: PaymentMethod
  amount: number
  referenceNumber: string | null
  receivedAmount: number | null
  changeAmount: number | null
}

export interface SaleSummaryDto {
  id: string
  saleNumber: string
  branchId: string
  branchName: string
  cashierUserId: string
  cashierName: string
  itemCount: number
  grandTotal: number
  status: SaleStatus
  paymentSummary: string
  createdAtUtc: string
}

export interface SaleReturnItemDto {
  id: string
  saleItemId: string
  productVariantId: string
  productName: string
  quantity: number
  refundAmount: number
  restocked: boolean
}

export interface ReceiptPaymentDto {
  method: string
  amount: number
}

export interface SaleReturnDto {
  id: string
  returnNumber: string
  saleId: string
  originalSaleNumber: string
  reason: string
  totalRefund: number
  createdByUserId: string
  createdByName: string
  createdAtUtc: string
  items: SaleReturnItemDto[]
  refunds: ReceiptPaymentDto[]
}

export interface SaleDetailDto {
  sale: SaleSummaryDto
  registerSessionId: string
  subtotal: number
  discountTotal: number
  taxTotal: number
  amountPaid: number
  changeDue: number
  completedAtUtc: string | null
  items: SaleItemDto[]
  payments: SalePaymentDto[]
  returns: SaleReturnDto[]
}

export interface SaleListParams {
  branchId?: string
  registerId?: string
  cashierUserId?: string
  status?: SaleStatus
  fromUtc?: string
  toUtc?: string
  search?: string
  page?: number
  pageSize?: number
}

// ---- Receipt ----

export interface ReceiptLineDto {
  description: string
  variantName: string | null
  quantity: number
  unitPrice: number
  netAmount: number
}

export interface ReceiptDto {
  storeName: string
  branchName: string
  registerName: string
  saleNumber: string
  cashierName: string
  createdAtUtc: string
  lines: ReceiptLineDto[]
  subtotal: number
  discountTotal: number
  taxTotal: number
  grandTotal: number
  payments: ReceiptPaymentDto[]
  changeDue: number
  status: SaleStatus
}

// ---- Returns ----

export interface ReturnLineInput {
  saleItemId: string
  quantity: number
  restock: boolean
}

export interface CreateReturnRequest {
  items: ReturnLineInput[]
  reason: string
  refundMethod: PaymentMethod
  refundReference: string | null
}

// ---- Tenant settings — tax ----
// Backend contract: GET /api/settings/tax -> TaxSettingsDto ; PUT /api/settings/tax (TenantSettingsWrite).
// `taxRatePercent` is percentage points (e.g. 12 = 12%), 0–100. The POS cart preview and the
// server-side SaleLineCalculator both treat it as `taxable * taxRatePercent / 100`.

export interface TaxSettingsDto {
  taxRatePercent: number
  pricesIncludeTax: boolean
}

export interface UpdateTaxSettingsRequest {
  taxRatePercent: number
  pricesIncludeTax: boolean
}

// ---- Phase 4: Staff & access management ----
// One email = one Negosio account = one tenant (unchanged). Staff are invited by an Owner/Admin,
// set their own name + password on acceptance, and hold exactly one role.

export type StaffMemberKind = 'Member' | 'Invitation'

export type StaffMemberStatus = 'Active' | 'Deactivated' | 'Invited' | 'Expired'

export interface StaffMemberDto {
  id: string
  kind: StaffMemberKind
  firstName: string | null
  lastName: string | null
  email: string
  role: UserRole
  status: StaffMemberStatus
  joinedAtUtc: string | null
  invitedAtUtc: string | null
  expiresAtUtc: string | null
  invitedByName: string | null
}

export interface InviteStaffRequest {
  email: string
  role: UserRole
}

export interface ChangeStaffRoleRequest {
  role: UserRole
}

/** `acceptPath` (e.g. "/invite/<token>") is returned outside Production only — no email provider yet. */
export interface StaffInvitationResultDto {
  invitationId: string
  email: string
  role: UserRole
  expiresAtUtc: string
  acceptPath: string | null
}

export interface InvitationPreviewDto {
  businessName: string
  email: string
  role: UserRole
  expiresAtUtc: string
}

export interface AcceptInvitationRequest {
  firstName: string
  lastName: string
  password: string
}

export interface AcceptInvitationResultDto {
  email: string
}
