namespace Negosio.Domain.Enums;

/// <summary>
/// Kind of human-readable document number allocated from a per-(tenant, branch) counter.
/// Phase 3 sales <b>and</b> returns share one branch transaction sequence — both allocate with
/// <see cref="Sale"/>. <see cref="Return"/> is retained for value stability but is no longer used
/// for allocation. The rest are reserved so later phases can allocate numbers without a schema
/// change. Persisted numerically; values must stay stable.
/// </summary>
public enum DocumentNumberType
{
    Sale = 1,
    Return = 2,
    PurchaseOrder = 3,
    StockTransfer = 4
}
