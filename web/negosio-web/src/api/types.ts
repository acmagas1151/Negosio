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
  addressLine1: string
  addressLine2: string | null
  city: string
  province: string
  postalCode: string | null
  isActive: boolean
  createdAtUtc: string
  assignedStaffCount: number
}

export interface CreateBranchRequest {
  name: string
  code: string
  addressLine1: string
  addressLine2: string | null
  city: string
  province: string
  postalCode: string | null
}

export type UpdateBranchRequest = Omit<CreateBranchRequest, 'code'>

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

export interface RegisterOpenSessionDto {
  sessionId: string
  openedByUserId: string
  openedByName: string
  openedAtUtc: string
  openingCash: number
}

export interface RegisterDto {
  id: string
  branchId: string
  branchName: string
  name: string
  code: string
  isActive: boolean
  createdAtUtc: string
  updatedAtUtc: string
  openSession: RegisterOpenSessionDto | null
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
  grossCashSales: number | null
  voidedCashSales: number | null
  refundCashOut: number | null
  cashIn: number | null
  cashOut: number | null
}

export type CashMovementType = 'CashIn' | 'CashOut'

export interface RegisterCashMovementDto {
  id: string
  type: CashMovementType
  amount: number
  reason: string
  createdByUserId: string
  createdByName: string
  createdAtUtc: string
}

export interface CreateCashMovementRequest {
  type: CashMovementType
  amount: number
  reason: string
}

export interface OpenRegisterSessionRequest {
  registerId: string
  openingCash: number
}

export interface CloseRegisterSessionRequest {
  closingCash: number
}

// ---- POS: branch/register context ----

export interface PosBranchDto {
  id: string
  name: string
  code: string
}

export interface PosContextDto {
  branchId: string | null
  branchName: string | null
  canPickBranch: boolean
  branches: PosBranchDto[]
}

export interface PosOpenSessionDto {
  sessionId: string
  openedByUserId: string
  openedByName: string
  mine: boolean
}

export interface PosRegisterDto {
  id: string
  name: string
  code: string
  isActive: boolean
  openSession: PosOpenSessionDto | null
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
  categoryId?: string
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
  /** Manager/Admin/Owner approval — only sent when retrying after a DISCOUNT_APPROVAL_REQUIRED
   * rejection (the sale carries a line discount the cashier can't apply directly). */
  approval?: VoidSaleApprovalInput
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
  approvedByUserId: string | null
  approvedByName: string | null
  createdAtUtc: string
  items: SaleReturnItemDto[]
  refunds: ReceiptPaymentDto[]
}

export interface VoidSaleApprovalInput {
  approverEmail: string
  approverPassword: string
}

export interface VoidSaleRequest {
  reason: string
  approval?: VoidSaleApprovalInput
}

/** Authorizes cancelling the cart at the register — no sale exists yet, so there is no reason to record. */
export interface CancelTransactionAuthorizationRequest {
  branchId: string
  approval?: VoidSaleApprovalInput
}

/** Authorizes + audits a no-sale cash-drawer open. No hardware/device integration exists yet — this
 * never claims a physical drawer opened, only that the request was authorized and recorded. */
export interface OpenCashDrawerRequest {
  approval?: VoidSaleApprovalInput
}

export interface CashDrawerOpenDto {
  id: string
  registerSessionId: string
  requestedByUserId: string
  requestedByName: string
  approvedByUserId: string | null
  approvedByName: string | null
  createdAtUtc: string
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
  voidedByUserId: string | null
  voidedByName: string | null
  approvedByUserId: string | null
  approvedByName: string | null
  voidReason: string | null
  voidedAtUtc: string | null
  canVoid: boolean
  voidIneligibilityCode: string | null
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
  /** Manager/Admin/Owner approval — only sent when retrying after a RETURN_APPROVAL_REQUIRED
   * rejection (the cashier doesn't hold a direct SalesReturn grant). */
  approval?: VoidSaleApprovalInput
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
  branchId: string | null
  branchName: string | null
  salesVoid: boolean
  salesReturn: boolean
  discountApply: boolean
  cashDrawerOpen: boolean
}

/** One flag per grantable permission — always sent together, so "Save changes" in the permissions
 * modal is a single request carrying every toggle's current state. */
export interface ChangeStaffPermissionsRequest {
  salesVoid: boolean
  salesReturn: boolean
  discountApply: boolean
  cashDrawerOpen: boolean
}

export interface InviteStaffRequest {
  email: string
  role: UserRole
  branchId?: string | null
}

export interface ChangeStaffRoleRequest {
  role: UserRole
  branchId?: string | null
}

export interface ChangeStaffBranchRequest {
  branchId: string
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

// ---- Reports ----
// Backend contract: GET /api/reports/overview | /top-products | /categories (ReportsController).
// Every figure here is a backend-computed aggregate — the frontend never recomputes a total from
// raw rows. Boundaries for a `period` preset are resolved server-side in the business's own
// (Philippines, fixed UTC+8) local day — never the browser's timezone.

export type ReportPeriod = 'Today' | 'Yesterday' | 'Last7Days' | 'Last30Days' | 'ThisMonth' | 'Custom'

/** Shared filter for every report request. `fromDate`/`toDate` (yyyy-MM-dd) are only read when
 * `period` is 'Custom'. */
export interface ReportFilterParams {
  period: ReportPeriod
  fromDate?: string
  toDate?: string
  branchId?: string
  registerId?: string
  cashierId?: string
}

export interface ReportKpiDto {
  grossSales: number
  discounts: number
  tax: number
  netSales: number
  returns: number
  netCollected: number
  completedTransactions: number
  averageTransactionValue: number
  totalItemsSold: number
  voidedSalesCount: number
  voidedSalesValue: number
  returnsCount: number
}

/** Percent change vs. the immediately preceding period of equal length. `null` — never a fabricated
 * figure — when the previous period was zero and there's nothing meaningful to divide by. */
export interface ReportKpiComparisonDto {
  grossSalesChangePercent: number | null
  netSalesChangePercent: number | null
  transactionsChangePercent: number | null
  averageTransactionChangePercent: number | null
  returnsChangePercent: number | null
}

/** `bucketLabel` is pre-formatted server-side in business-local time ("9 AM", "Sep 9") — render it
 * verbatim, never re-parse/re-convert `bucketStartUtc` for display. */
export interface SalesTrendPointDto {
  bucketLabel: string
  bucketStartUtc: string
  netSales: number
  transactions: number
}

/** `paymentCount` counts payment *records*, not sales — a split-tender sale contributes to more
 * than one method here, by design. */
export interface PaymentMethodBreakdownDto {
  method: PaymentMethod
  amount: number
  paymentCount: number
  percentage: number
}

export interface ReportsOverviewDto {
  fromUtc: string
  toUtc: string
  kpis: ReportKpiDto
  comparison: ReportKpiComparisonDto
  trendIsHourly: boolean
  trend: SalesTrendPointDto[]
  paymentMethods: PaymentMethodBreakdownDto[]
}

/** Grouped by the stable productVariantId — product/variant/SKU/category shown are the *current*
 * catalog values (a live join), not a point-in-time snapshot. */
export interface TopProductDto {
  productVariantId: string
  productName: string
  variantName: string | null
  sku: string | null
  categoryName: string | null
  quantitySold: number
  salesAmount: number
}

/** Grouped by the product's *current* category — a product recategorized after the sale reports
 * under its new category (no per-sale category snapshot exists). */
export interface CategoryPerformanceDto {
  categoryId: string | null
  categoryName: string
  quantitySold: number
  salesAmount: number
  percentageOfSales: number
}
