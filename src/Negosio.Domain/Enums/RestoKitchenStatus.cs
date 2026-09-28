namespace Negosio.Domain.Enums;

/// <summary><see cref="Pending"/> means "released, not yet acknowledged" — distinct from
/// <see cref="Acknowledged"/>, which requires an actual kitchen-side action. See design spec
/// Section 6.2.</summary>
public enum RestoKitchenStatus
{
    Pending = 1,
    Acknowledged = 2,
    Ready = 3,
    Served = 4,
}
