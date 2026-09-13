namespace Negosio.Domain.Enums;

/// <summary>
/// Lifecycle of fulfillment. Persisted numerically on a schedule row; values must stay stable.
/// <para><see cref="Unscheduled"/> is NEVER persisted on a schedule row — a row is always Pending,
/// Completed or Cancelled. Unscheduled describes quantity that is intended for a method but not yet
/// on any schedule, and exists here so the UI and reports share one vocabulary (the spec's label
/// table maps Delivery+Unscheduled to "Deliver later", Pickup+Unscheduled to "Pickup not
/// scheduled").</para>
/// <para>Pending → Completed and Pending → Cancelled are the only transitions; both are final. A
/// cancelled schedule is never reactivated — its quantities are released or converted, and any
/// replacement is a brand-new row.</para>
/// </summary>
public enum FulfillmentStatus
{
    Unscheduled = 1,
    Pending = 2,
    Completed = 3,
    Cancelled = 4
}
