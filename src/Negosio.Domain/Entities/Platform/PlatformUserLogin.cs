using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// The global login directory (platform database). Resolves an email to its tenant so the correct
/// tenant database can be selected, and holds the authoritative password hash + role for issuing a
/// JWT without touching any tenant database. <see cref="Id"/> equals the tenant-database
/// <c>User.Id</c> so the JWT <c>sub</c> claim stays stable across the split.
/// </summary>
public class PlatformUserLogin : Entity
{
    private PlatformUserLogin()
    {
        EmailNormalized = string.Empty;
        PasswordHash = string.Empty;
    }

    private PlatformUserLogin(Guid id, Guid tenantId, string emailNormalized, string passwordHash, UserRole role)
    {
        Id = id;
        TenantId = tenantId;
        EmailNormalized = emailNormalized;
        PasswordHash = passwordHash;
        Role = role;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Trimmed, lower-cased email. Globally unique (one email = one login = one tenant).</summary>
    public string EmailNormalized { get; private set; }

    public string PasswordHash { get; private set; }

    public UserRole Role { get; private set; }

    public bool IsActive { get; private set; }

    public static PlatformUserLogin Create(Guid id, Guid tenantId, string email, string passwordHash, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email is required.", nameof(email));
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Password hash is required.", nameof(passwordHash));
        }

        return new PlatformUserLogin(id, tenantId, Normalize(email), passwordHash, role);
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

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    /// <summary>Same rule as the tenant <c>User.NormalizeEmail</c>.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
