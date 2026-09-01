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

    private User(Guid id, Guid tenantId, string email, string firstName, string lastName, UserRole role, Guid? branchId)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        FirstName = firstName;
        LastName = lastName;
        Role = role;
        BranchId = branchId;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    /// <summary>Normalized (trimmed, lower-cased) email. Use <see cref="NormalizeEmail"/> before comparing.</summary>
    public string Email { get; private set; }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public UserRole Role { get; private set; }

    /// <summary>
    /// The branch this user is bound to. Non-null for branch-scoped roles, null for Owner/Admin.
    /// </summary>
    public Guid? BranchId { get; private set; }

    public bool IsActive { get; private set; }

    public static User Create(
        Guid id,
        Guid tenantId,
        string normalizedEmail,
        string firstName,
        string lastName,
        UserRole role,
        Guid? branchId = null)
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

        return new User(id, tenantId, NormalizeEmail(normalizedEmail), firstName.Trim(), lastName.Trim(), role, branchId);
    }

    /// <summary>Bind this user to a branch (branch-scoped roles).</summary>
    public void AssignBranch(Guid branchId)
    {
        BranchId = branchId;
        Touch();
    }

    /// <summary>Clear the branch assignment (when a user becomes Owner/Admin).</summary>
    public void ClearBranch()
    {
        BranchId = null;
        Touch();
    }

    /// <summary>Change this user's role. The Owner role is never assignable through this path.</summary>
    public void ChangeRole(UserRole role)
    {
        if (role == UserRole.Owner)
        {
            throw new InvalidOperationException("The Owner role cannot be assigned.");
        }

        if (Role == UserRole.Owner)
        {
            throw new InvalidOperationException("An Owner's role cannot be changed.");
        }

        Role = role;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void Reactivate()
    {
        IsActive = true;
        Touch();
    }

    /// <summary>Single source of truth for email normalization used when saving and when comparing.</summary>
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
