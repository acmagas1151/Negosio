using Negosio.Domain.Enums;

namespace Negosio.Application.Abstractions;

/// <summary>
/// The authenticated principal for the current request. The implementation reads claims from the
/// validated JWT; client-supplied values (e.g. a TenantId in a request body) must never override it.
/// </summary>
public interface ICurrentUser
{
    Guid UserId { get; }

    Guid TenantId { get; }

    string Email { get; }

    UserRole Role { get; }

    bool IsAuthenticated { get; }
}
