using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Abstractions;

public sealed record AccessToken(string Value, DateTime ExpiresAtUtc);

/// <summary>The logical identity a JWT is minted for. No secrets.</summary>
public sealed record TokenSubject(Guid UserId, Guid TenantId, UserRole Role, string Email);

/// <summary>Issues signed JWT access tokens for authenticated users.</summary>
public interface IJwtTokenGenerator
{
    AccessToken Generate(TokenSubject subject);

    AccessToken Generate(User user) => Generate(new TokenSubject(user.Id, user.TenantId, user.Role, user.Email));
}
