namespace Negosio.Domain.Enums;

/// <summary>Lifecycle of a cash-drawer session. Persisted numerically; values must stay stable.</summary>
public enum RegisterSessionStatus
{
    Open = 1,
    Closed = 2
}
