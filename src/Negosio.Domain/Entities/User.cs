using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// An account that belongs to exactly one tenant. Passwords are never stored in plaintext;
/// <see cref="PasswordHash"/> holds an output of a reputable password hasher.
/// </summary>
public class User : Entity
{
    private User()
    {
        Email = string.Empty;
        PasswordHash = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    private User(
        Guid tenantId,
        string email,
        string passwordHash,
        string firstName,
        string lastName,
        UserRole role)
    {
        TenantId = tenantId;
        Email = email;
        PasswordHash = passwordHash;
        FirstName = firstName;
        LastName = lastName;
        Role = role;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Tenant Tenant { get; private set; } = null!;

    /// <summary>Normalized (trimmed, lower-cased) email. Use <see cref="NormalizeEmail"/> before comparing.</summary>
    public string Email { get; private set; }

    public string PasswordHash { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public UserRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public static User Create(
        Guid tenantId,
        string normalizedEmail,
        string passwordHash,
        string firstName,
        string lastName,
        UserRole role)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            throw new ArgumentException("Email is required.", nameof(normalizedEmail));
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        if (string.IsNullOrWhiteSpace(firstName))
        {
            throw new ArgumentException("First name is required.", nameof(firstName));
        }

        if (string.IsNullOrWhiteSpace(lastName))
        {
            throw new ArgumentException("Last name is required.", nameof(lastName));
        }

        return new User(tenantId, NormalizeEmail(normalizedEmail), passwordHash, firstName.Trim(), lastName.Trim(), role);
    }

    public void SetPasswordHash(string passwordHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
        Touch();
    }

    /// <summary>Single source of truth for email normalization used when saving and when comparing.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
