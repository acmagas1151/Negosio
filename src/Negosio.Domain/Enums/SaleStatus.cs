namespace Negosio.Domain.Enums;

/// <summary>
/// State of a completed sale. Persisted numerically; values must stay stable.
/// Phase 3 produces <see cref="Completed"/>, <see cref="Refunded"/> and <see cref="PartiallyRefunded"/>;
/// <see cref="Voided"/> is reserved for a later void workflow.
/// </summary>
public enum SaleStatus
{
    Completed = 1,
    Voided = 2,
    Refunded = 3,
    PartiallyRefunded = 4
}
