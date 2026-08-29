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
}
