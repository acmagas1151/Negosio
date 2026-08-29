namespace Negosio.Domain.Enums;

/// <summary>
/// Supported vertical for a tenant. Values are persisted numerically, so they must remain stable.
/// </summary>
public enum BusinessType
{
    Retail = 1,
    FoodAndBeverage = 2,
    DiagnosticCenter = 3
}
