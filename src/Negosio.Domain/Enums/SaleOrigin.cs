namespace Negosio.Domain.Enums;

/// <summary>Set once at <see cref="Negosio.Domain.Entities.Sale"/> creation, never changed. The one
/// discriminator that lets <c>ReturnService</c> reject a return against a Resto sale — see design
/// spec Section 7.</summary>
public enum SaleOrigin
{
    Retail = 1,
    Resto = 2,
}
