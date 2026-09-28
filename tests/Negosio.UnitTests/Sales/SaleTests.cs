using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Sales;

public class SaleTests
{
    [Fact]
    public void Begin_defaults_Origin_to_Retail()
    {
        var sale = Sale.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid());

        sale.Origin.Should().Be(SaleOrigin.Retail);
    }

    [Fact]
    public void Begin_accepts_an_explicit_Resto_origin()
    {
        var sale = Sale.Begin(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "0000001", Guid.NewGuid(), Guid.NewGuid(),
            origin: SaleOrigin.Resto);

        sale.Origin.Should().Be(SaleOrigin.Resto);
    }
}
