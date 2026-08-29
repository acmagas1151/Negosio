namespace Negosio.Domain.Enums;

/// <summary>
/// How a sale (or refund) was paid. Phase 3 records these only — no external gateway integration.
/// Persisted numerically; values must stay stable.
/// </summary>
public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    GCash = 3,
    Maya = 4,
    BankTransfer = 5,
    Other = 99
}
