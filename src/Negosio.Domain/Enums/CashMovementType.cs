namespace Negosio.Domain.Enums;

/// <summary>Direction of a register cash movement. Persisted numerically; values must stay stable.</summary>
public enum CashMovementType
{
    CashIn = 1,
    CashOut = 2
}
