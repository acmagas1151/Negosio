using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A user profile inside a tenant database. Carries name and role; the authoritative credentials
/// live in the platform <c>PlatformUserLogin</c> record (same <see cref="Entity.Id"/>).
/// </summary>
public class User : Entity
{
    private User()
    {
        Email = string.Empty;
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    private User(Guid id, Guid tenantId, string email, string firstName, string lastName, UserRole role)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        FirstName = firstName;
        LastName = lastName;
        Role = role;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Normalized (trimmed, lower-cased) email. Use <see cref="NormalizeEmail"/> before comparing.</summary>
    public string Email { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public UserRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public static User Create(
        Guid id,
        Guid tenantId,
        string normalizedEmail,
        string firstName,
        string lastName,
        UserRole role)
    {
        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            throw new ArgumentException("Email is required.", nameof(normalizedEmail));
        }

        if (string.IsNullOrWhiteSpace(firstName))
        {
            throw new ArgumentException("First name is required.", nameof(firstName));
        }

        if (string.IsNullOrWhiteSpace(lastName))
        {
            throw new ArgumentException("Last name is required.", nameof(lastName));
        }

        return new User(id, tenantId, NormalizeEmail(normalizedEmail), firstName.Trim(), lastName.Trim(), role);
    }

    /// <summary>Single source of truth for email normalization used when saving and when comparing.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
