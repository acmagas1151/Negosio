using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Domain;

public class TenantProfileTests
{
    [Fact]
    public void ConfigureBusinessInfo_trims_and_nulls_empty()
    {
        var p = TenantProfile.Create(Guid.NewGuid(), "Acme", BusinessType.Retail);
        p.ConfigureBusinessInfo("  0917 000 1234 ", "   ");
        p.ContactNumber.Should().Be("0917 000 1234");
        p.TaxId.Should().BeNull();
    }
}
