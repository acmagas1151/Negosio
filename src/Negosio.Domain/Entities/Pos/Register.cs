using Negosio.Domain.Common;

namespace Negosio.Domain.Entities;

/// <summary>
/// A physical or logical POS terminal at a branch (e.g. "Main Counter", "Register 1"). Sales are
/// always made through a <see cref="RegisterSession"/> opened on a register.
/// <see cref="Code"/> is unique within a branch.
/// </summary>
public class Register : Entity
{
    private Register()
    {
        Name = string.Empty;
        Code = string.Empty;
    }

    private Register(Guid tenantId, Guid branchId, string name, string code)
    {
        TenantId = tenantId;
        BranchId = branchId;
        Name = name;
        Code = code;
        IsActive = true;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public string Name { get; private set; }

    public string Code { get; private set; }

    public bool IsActive { get; private set; }

    public static Register Create(Guid tenantId, Guid branchId, string name, string code) =>
        new(tenantId, branchId, RequireName(name), RequireCode(code));

    public void UpdateDetails(string name, string code)
    {
        Name = RequireName(name);
        Code = RequireCode(code);
        Touch();
    }

    public void Activate()
    {
        if (IsActive)
        {
            return;
        }

        IsActive = true;
        Touch();
    }

    public void Deactivate()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        Touch();
    }

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Register name is required.", nameof(name))
            : name.Trim();

    private static string RequireCode(string code) =>
        string.IsNullOrWhiteSpace(code)
            ? throw new ArgumentException("Register code is required.", nameof(code))
            : code.Trim().ToUpperInvariant();
}
