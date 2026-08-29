using Negosio.Domain.Entities;

namespace Negosio.Application.Abstractions;

public sealed record AccessToken(string Value, DateTime ExpiresAtUtc);

/// <summary>Issues signed JWT access tokens for authenticated users.</summary>
public interface IJwtTokenGenerator
{
    AccessToken Generate(User user);
}
