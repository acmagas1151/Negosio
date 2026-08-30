namespace Negosio.Application.Common;

/// <summary>
/// Base class for expected, client-facing failures. Each carries a stable machine-readable
/// <see cref="Code"/> and the HTTP <see cref="StatusCode"/> the API should return.
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string code, string message, int statusCode)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }

    public int StatusCode { get; }

    /// <summary>Optional per-field validation messages.</summary>
    public IReadOnlyDictionary<string, string[]>? Errors { get; protected init; }
}

/// <summary>400 - a request failed input validation.</summary>
public sealed class ValidationAppException : AppException
{
    public ValidationAppException(IReadOnlyDictionary<string, string[]> errors)
        : base(ErrorCodes.ValidationFailed, "One or more validation errors occurred.", StatusCodes.Status400BadRequest)
    {
        Errors = errors;
    }
}

/// <summary>400 - a business rule rejected an otherwise well-formed request.</summary>
public sealed class BusinessRuleException : AppException
{
    public BusinessRuleException(string code, string message)
        : base(code, message, StatusCodes.Status400BadRequest)
    {
    }
}

/// <summary>409 - the request conflicts with existing state (e.g. duplicate email).</summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string code, string message)
        : base(code, message, StatusCodes.Status409Conflict)
    {
    }
}

/// <summary>404 - the requested resource does not exist (or is not visible to this tenant).</summary>
public sealed class NotFoundException : AppException
{
    public NotFoundException(string message)
        : base(ErrorCodes.NotFound, message, StatusCodes.Status404NotFound)
    {
    }

    public NotFoundException(string code, string message)
        : base(code, message, StatusCodes.Status404NotFound)
    {
    }
}

/// <summary>401 - authentication failed or is missing.</summary>
public sealed class UnauthorizedAppException : AppException
{
    public UnauthorizedAppException(string message)
        : base(ErrorCodes.Unauthorized, message, StatusCodes.Status401Unauthorized)
    {
    }
}

/// <summary>403 - the tenant is not in a state that permits operations (suspended, provisioning, failed).</summary>
public sealed class TenantUnavailableException : AppException
{
    public TenantUnavailableException(string code, string message)
        : base(code, message, StatusCodes.Status403Forbidden)
    {
    }
}

/// <summary>500 - provisioning a new tenant's database did not complete.</summary>
public sealed class TenantProvisioningException : AppException
{
    public TenantProvisioningException(string message)
        : base(ErrorCodes.TenantProvisioningFailed, message, StatusCodes.Status500InternalServerError)
    {
    }
}

/// <summary>
/// Local mirror of the ASP.NET Core status code constants so the Application layer stays free of
/// a framework dependency while still expressing intent.
/// </summary>
internal static class StatusCodes
{
    public const int Status400BadRequest = 400;
    public const int Status401Unauthorized = 401;
    public const int Status403Forbidden = 403;
    public const int Status404NotFound = 404;
    public const int Status409Conflict = 409;
    public const int Status500InternalServerError = 500;
}
