namespace Negosio.Domain.Enums;

/// <summary>
/// Why a <c>StockMovement</c> happened. Values are persisted numerically, so they must remain stable.
/// Phase 2 actively produces only <see cref="OpeningStock"/>, <see cref="AdjustmentIncrease"/> and
/// <see cref="AdjustmentDecrease"/>; the rest exist for later phases (POS, transfers, purchasing).
/// </summary>
public enum StockMovementType
{
    OpeningStock = 1,
    AdjustmentIncrease = 2,
    AdjustmentDecrease = 3,
    Sale = 4,
    Return = 5,
    TransferIn = 6,
    TransferOut = 7,
    Purchase = 8,
    Waste = 9
}
