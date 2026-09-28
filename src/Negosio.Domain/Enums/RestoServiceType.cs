namespace Negosio.Domain.Enums;

/// <summary>Which of the two RestoPOS flows a <c>RestoOrder</c> follows. Fixed at creation, never
/// changed — see the design spec's Section 4 lifecycle rules.</summary>
public enum RestoServiceType
{
    PayAsYouOrder = 1,
    BillOut = 2,
}
