namespace Negosio.Application.Common;

/// <summary>Stable, machine-readable error codes returned in the API error envelope.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string InternalError = "INTERNAL_ERROR";

    // ---- Phase 2.5: database-per-tenant ----
    public const string TenantNotFound = "TENANT_NOT_FOUND";
    public const string TenantUnavailable = "TENANT_UNAVAILABLE";
    public const string TenantSuspended = "TENANT_SUSPENDED";
    public const string TenantProvisioningFailed = "TENANT_PROVISIONING_FAILED";

    public const string DuplicateEmail = "DUPLICATE_EMAIL";
    public const string BusinessTypeNotAvailable = "BUSINESS_TYPE_NOT_AVAILABLE";
    public const string InvalidCredentials = "INVALID_CREDENTIALS";
    public const string AccountInactive = "ACCOUNT_INACTIVE";
    public const string DuplicateBranchCode = "DUPLICATE_BRANCH_CODE";

    // ---- Phase 2: Catalog + Inventory ----
    public const string CategoryNotFound = "CATEGORY_NOT_FOUND";
    public const string CategoryAlreadyExists = "CATEGORY_ALREADY_EXISTS";
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";
    public const string SkuAlreadyExists = "SKU_ALREADY_EXISTS";
    public const string BarcodeAlreadyExists = "BARCODE_ALREADY_EXISTS";
    public const string VariantNotFound = "VARIANT_NOT_FOUND";
    public const string VariantRequired = "VARIANT_REQUIRED";
    public const string ProductHasVariants = "PRODUCT_HAS_VARIANTS";
    public const string InventoryNotFound = "INVENTORY_NOT_FOUND";
    public const string InvalidInventoryAdjustment = "INVALID_INVENTORY_ADJUSTMENT";
    public const string InsufficientInventory = "INSUFFICIENT_INVENTORY";
    public const string InventoryConcurrencyConflict = "INVENTORY_CONCURRENCY_CONFLICT";
    public const string BranchNotFound = "BRANCH_NOT_FOUND";

    // ---- Phase 3: Retail POS ----
    public const string RegisterNotFound = "REGISTER_NOT_FOUND";
    public const string RegisterAlreadyExists = "REGISTER_ALREADY_EXISTS";
    public const string RegisterSessionAlreadyOpen = "REGISTER_SESSION_ALREADY_OPEN";
    public const string RegisterSessionNotOpen = "REGISTER_SESSION_NOT_OPEN";
    public const string RegisterSessionNotFound = "REGISTER_SESSION_NOT_FOUND";
    public const string SaleNotFound = "SALE_NOT_FOUND";
    public const string SaleAlreadyProcessed = "SALE_ALREADY_PROCESSED";
    public const string InvalidSaleItem = "INVALID_SALE_ITEM";
    public const string InvalidQuantity = "INVALID_QUANTITY";
    public const string CheckoutConcurrencyConflict = "CHECKOUT_CONCURRENCY_CONFLICT";
    public const string PaymentInsufficient = "PAYMENT_INSUFFICIENT";
    public const string InvalidPayment = "INVALID_PAYMENT";
    public const string InvalidDiscount = "INVALID_DISCOUNT";
    public const string ReturnNotAllowed = "RETURN_NOT_ALLOWED";
    public const string ReturnQuantityExceeded = "RETURN_QUANTITY_EXCEEDED";
    public const string DuplicateCheckoutRequest = "DUPLICATE_CHECKOUT_REQUEST";

    // ---- Phase 4: Staff & access management ----
    public const string StaffNotFound = "STAFF_NOT_FOUND";
    public const string InvitationNotFound = "INVITATION_NOT_FOUND";
    public const string InvitationInvalid = "INVITATION_INVALID";        // expired / revoked / already accepted
    public const string InvitationAlreadyAccepted = "INVITATION_ALREADY_ACCEPTED";
    public const string StaffEmailInUse = "STAFF_EMAIL_IN_USE";          // email already has a login (this or another tenant)
    public const string StaffAlreadyInvited = "STAFF_ALREADY_INVITED";
    public const string RoleNotAssignable = "ROLE_NOT_ASSIGNABLE";       // acting user may not grant this role
    public const string OwnerRoleForbidden = "OWNER_ROLE_FORBIDDEN";     // Owner is never assignable (no transfer yet)
    public const string OwnerProtected = "OWNER_PROTECTED";              // Admin cannot act on an Owner
    public const string StaffSelfAction = "STAFF_SELF_ACTION";           // cannot deactivate / re-role yourself
    public const string LastOwner = "LAST_OWNER";                        // cannot remove the tenant's last Owner
    public const string StaffAlreadyActive = "STAFF_ALREADY_ACTIVE";
    public const string StaffAlreadyDeactivated = "STAFF_ALREADY_DEACTIVATED";
    public const string SessionStale = "SESSION_STALE";                  // JWT role/active state no longer matches — sign in again

    // ---- Phase 5: Branch management & branch-scoped access ----
    public const string LastActiveBranch = "LAST_ACTIVE_BRANCH";         // cannot deactivate the tenant's last active branch
    public const string BranchInactive = "BRANCH_INACTIVE";              // assigned / target branch is inactive
    public const string BranchForbidden = "BRANCH_FORBIDDEN";            // branch-scoped user targeted a branch that is not theirs
    public const string SessionNotOwned = "SESSION_NOT_OWNED";           // operating / closing another user's register session
    public const string CashierSessionOpen = "CASHIER_SESSION_OPEN";     // user already has an open register session
    public const string StaffHasOpenRegisterSession = "STAFF_HAS_OPEN_REGISTER_SESSION"; // cannot reassign while a session is open

    // ---- Phase 6: Void sales & cash operations ----
    public const string SaleNotVoidable = "SALE_NOT_VOIDABLE";
    public const string SaleHasReturns = "SALE_HAS_RETURNS";
    public const string VoidSessionClosed = "VOID_SESSION_CLOSED";
    public const string VoidCutoffExpired = "VOID_CUTOFF_EXPIRED";
    public const string VoidApprovalRequired = "VOID_APPROVAL_REQUIRED";
    public const string InvalidApproverCredentials = "INVALID_APPROVER_CREDENTIALS";
    public const string VoidApproverNotAuthorized = "VOID_APPROVER_NOT_AUTHORIZED";
    public const string VoidApproverWrongBranch = "VOID_APPROVER_WRONG_BRANCH";
    public const string SalesVoidSelfGrant = "SALES_VOID_SELF_GRANT";
    public const string SalesVoidGrantRoleInvalid = "SALES_VOID_GRANT_ROLE_INVALID";
    public const string CashMovementInvalidAmount = "CASH_MOVEMENT_INVALID_AMOUNT";
    public const string CashMovementSessionClosed = "CASH_MOVEMENT_SESSION_CLOSED";
    public const string CashMovementNotOwner = "CASH_MOVEMENT_NOT_OWNER";
}
