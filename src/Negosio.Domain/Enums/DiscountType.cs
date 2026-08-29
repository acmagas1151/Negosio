namespace Negosio.Domain.Enums;

/// <summary>Line-level discount kind for Phase 3. Persisted numerically; values must stay stable.</summary>
public enum DiscountType
{
    None = 0,
    FixedAmount = 1,
    Percentage = 2
}
