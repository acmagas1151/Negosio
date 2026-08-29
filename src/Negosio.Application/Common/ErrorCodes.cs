namespace Negosio.Application.Common;

/// <summary>Stable, machine-readable error codes returned in the API error envelope.</summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string NotFound = "NOT_FOUND";
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string InternalError = "INTERNAL_ERROR";

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
}
