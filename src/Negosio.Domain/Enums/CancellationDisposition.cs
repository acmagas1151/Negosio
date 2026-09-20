namespace Negosio.Domain.Enums;

/// <summary>
/// What happens to a cancelled schedule's quantities. Required on every cancellation — the quantities
/// have to go somewhere, and leaving that implicit is how they get silently stranded.
/// <para>Valid pairings are enforced server-side: a Delivery may be cancelled with
/// <see cref="DeliverLater"/>, <see cref="ConvertToPickup"/> or <see cref="CustomerPickedUpInstead"/>;
/// a Pickup with <see cref="PickupLater"/> or <see cref="ConvertToDelivery"/>. There is deliberately
/// no TakeNow disposition: TakeNow means "received during checkout", which cannot become true after
/// the fact. A customer physically collecting items is
/// <see cref="CustomerPickedUpInstead"/> (which produces a Completed Pickup), never TakeNow.</para>
/// </summary>
public enum CancellationDisposition
{
    /// <summary>Delivery only. Quantities move to a brand-new, immediately-Pending Delivery scheduled for
    /// a later date — the same method, just rescheduled — created in the same transaction as the
    /// cancellation.</summary>
    DeliverLater = 1,

    /// <summary>Pickup only. Quantities move to a brand-new, immediately-Pending Pickup scheduled for a
    /// later date — the same method, just rescheduled — created in the same transaction as the
    /// cancellation.</summary>
    PickupLater = 2,

    /// <summary>Pickup only. Quantities move to Delivery intent and a new Pending Delivery is created.</summary>
    ConvertToDelivery = 3,

    /// <summary>Delivery only. Quantities move to Pickup intent and a new Pending Pickup is created.</summary>
    ConvertToPickup = 4,

    /// <summary>Delivery only. The customer already collected the items: the Delivery is cancelled and a
    /// new, immediately-Completed (Claimed) Pickup records what actually happened. The Delivery is never
    /// marked Completed — it did not happen.</summary>
    CustomerPickedUpInstead = 5
}
