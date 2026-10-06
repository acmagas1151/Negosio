using Negosio.Domain.Enums;

namespace Negosio.Application.Resto;

/// <summary>
/// Decides whether a released RestoPOS item has consumed product, and therefore whether voiding it or
/// closing its order unpaid records waste. Pending means sent but not yet acknowledged, so nothing has
/// been started (design spec decision D2). Shared by item void and unpaid closure so they never disagree.
/// </summary>
public static class RestoWastePolicy
{
    public static bool IsConsumed(RestoKitchenStatus? status) =>
        status is RestoKitchenStatus.Acknowledged or RestoKitchenStatus.Ready or RestoKitchenStatus.Served;
}
