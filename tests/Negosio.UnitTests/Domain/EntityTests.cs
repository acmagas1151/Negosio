using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Domain;

public class EntityTests
{
    [Fact]
    public void Tenant_Create_trims_name_and_starts_pending()
    {
        var tenant = Tenant.Create("  Bruno's Cafe  ", BusinessType.FoodAndBeverage);

        tenant.Name.Should().Be("Bruno's Cafe");
        tenant.BusinessType.Should().Be(BusinessType.FoodAndBeverage);
        tenant.IsActive.Should().BeTrue();
        tenant.ProvisioningStatus.Should().Be(TenantProvisioningStatus.Pending);
        tenant.IsOperational.Should().BeFalse();
        tenant.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Tenant_becomes_operational_only_when_active_and_provisioned()
    {
        var tenant = Tenant.Create("Shop", BusinessType.Retail);

        tenant.MarkProvisioning();
        tenant.IsOperational.Should().BeFalse();

        tenant.MarkActive();
        tenant.IsOperational.Should().BeTrue();

        tenant.Suspend();
        tenant.IsOperational.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Tenant_Create_rejects_blank_name(string name)
    {
        var act = () => Tenant.Create(name, BusinessType.Retail);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void PlatformUserLogin_and_TenantProfile_carry_the_tenant_identity()
    {
        var tenantId = Guid.NewGuid();
        var login = PlatformUserLogin.Create(Guid.NewGuid(), tenantId, "  OWNER@Example.COM ", "hash", UserRole.Owner);
        var profile = TenantProfile.Create(tenantId, "  Shop ", BusinessType.Retail);

        login.EmailNormalized.Should().Be("owner@example.com");
        login.TenantId.Should().Be(tenantId);
        profile.Id.Should().Be(tenantId);
        profile.Name.Should().Be("Shop");
        profile.TaxRatePercent.Should().Be(0m);
        profile.PricesIncludeTax.Should().BeFalse();
    }

    [Fact]
    public void Branch_Create_uppercases_code_and_normalizes_optional_fields()
    {
        var branch = Branch.Create(Guid.NewGuid(), " Main ", " main ", " L1 ", "  ", " City ", " Province ", "  ");

        branch.Code.Should().Be("MAIN");
        branch.Name.Should().Be("Main");
        branch.AddressLine1.Should().Be("L1");
        branch.AddressLine2.Should().BeNull();
        branch.PostalCode.Should().BeNull();
    }

    [Fact]
    public void User_Create_normalizes_email()
    {
        var user = User.Create(Guid.NewGuid(), Guid.NewGuid(), "  OWNER@Example.COM ", "Ace", "Agas", UserRole.Owner);

        user.Email.Should().Be("owner@example.com");
        user.Role.Should().Be(UserRole.Owner);
        user.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("OWNER@EXAMPLE.COM", "owner@example.com")]
    [InlineData("  a@b.co  ", "a@b.co")]
    public void User_NormalizeEmail_is_lowercase_and_trimmed(string input, string expected)
    {
        User.NormalizeEmail(input).Should().Be(expected);
    }
}
