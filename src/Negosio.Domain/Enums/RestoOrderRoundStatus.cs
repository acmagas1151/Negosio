namespace Negosio.Domain.Enums;

/// <summary>Deliberately thin — a round only tracks whether it has been sent at all. The real
/// kitchen-workflow state lives per item, in <see cref="RestoKitchenStatus"/>, so that one round can
/// span multiple stations without a separate ticket entity per station.</summary>
public enum RestoOrderRoundStatus
{
    Draft = 1,
    Released = 2,
    Voided = 3,
}
