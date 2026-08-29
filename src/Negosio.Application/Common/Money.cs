namespace Negosio.Application.Common;

/// <summary>
/// Central monetary rounding for Phase 3. Money is always <see cref="decimal"/> (never float/double)
/// and stored as <c>decimal(18,2)</c>. Rounding is **half-up (away from zero) at 2 decimal places**,
/// applied per line and then summed, so displayed line totals always add up to the sale total.
/// </summary>
public static class Money
{
    public const int Decimals = 2;
    public const MidpointRounding Rounding = MidpointRounding.AwayFromZero;

    public static decimal Round(decimal value) => Math.Round(value, Decimals, Rounding);
}
