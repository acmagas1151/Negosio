using Microsoft.AspNetCore.Identity;
using Negosio.Application.Abstractions;
using Negosio.Domain.Entities;

namespace Negosio.Infrastructure.Security;

/// <summary>
/// Delegates to ASP.NET Core Identity's <see cref="PasswordHasher{TUser}"/> (PBKDF2, HMAC-SHA512,
/// 100k iterations by default in v3). No custom cryptography.
/// </summary>
public sealed class PasswordHasherAdapter : IPasswordHasher
{
    private readonly PasswordHasher<User> _inner = new();

    public string Hash(string password) => _inner.HashPassword(user: null!, password);

    public bool Verify(string password, string passwordHash)
    {
        var result = _inner.VerifyHashedPassword(user: null!, passwordHash, password);
        return result is PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
    }
}
