using Negosio.Domain.Common;
using Negosio.Domain.Enums;

namespace Negosio.Domain.Entities;

/// <summary>
/// A manual cash addition or removal against an open register session — float top-ups, petty
/// cash, bank deposits. Append-only, like <see cref="StockMovement"/>. Amount is always positive;
/// direction comes from <see cref="Type"/>, never a signed amount.
/// </summary>
public class RegisterCashMovement : Entity
{
    private RegisterCashMovement()
    {
    }

    private RegisterCashMovement(
        Guid tenantId, Guid branchId, Guid registerSessionId, CashMovementType type,
        decimal amount, string reason, Guid createdByUserId)
    {
        TenantId = tenantId;
        BranchId = branchId;
        RegisterSessionId = registerSessionId;
        Type = type;
        Amount = amount;
        Reason = reason;
        CreatedByUserId = createdByUserId;
    }

    public Guid TenantId { get; private set; }

    public Guid BranchId { get; private set; }

    public Guid RegisterSessionId { get; private set; }

    public CashMovementType Type { get; private set; }

    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public Guid CreatedByUserId { get; private set; }

    public static RegisterCashMovement Create(
        Guid tenantId, Guid branchId, Guid registerSessionId, CashMovementType type,
        decimal amount, string reason, Guid createdByUserId)
    {
        if (amount <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A reason is required.", nameof(reason));
        }

        return new RegisterCashMovement(tenantId, branchId, registerSessionId, type, amount, reason.Trim(), createdByUserId);
    }
}
