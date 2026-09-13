namespace Negosio.Domain.Enums;

/// <summary>
/// How a sold quantity reaches the customer. Chosen per sale line at checkout and, for the two
/// post-checkout methods, changeable afterwards only through an audited
/// <see cref="Entities.FulfillmentConversion"/>.
/// </summary>
public enum FulfillmentMethod
{
    /// <summary>The customer received the item during the original POS checkout. Completed
    /// immediately, never scheduled, and never a conversion target — it is a checkout-time method
    /// only.</summary>
    TakeNow = 1,

    /// <summary>The business transports the item to the customer.</summary>
    Delivery = 2,

    /// <summary>The customer collects the item after the original checkout.</summary>
    Pickup = 3
}
