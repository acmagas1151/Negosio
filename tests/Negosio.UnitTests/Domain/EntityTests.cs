using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Domain;

public class EntityTests
{
    [Fact]
    public void Tenant_Create_trims_name_and_activates()
    {
        var tenant = Tenant.Create("  Bruno's Cafe  ", BusinessType.FoodAndBeverage);

        tenant.Name.Should().Be("Bruno's Cafe");
        tenant.BusinessType.Should().Be(BusinessType.FoodAndBeverage);
        tenant.IsActive.Should().BeTrue();
        tenant.Id.Should().NotBeEmpty();
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
    public void Tenant_AddBranch_and_AddUser_populate_collections()
    {
        var tenant = Tenant.Create("Shop", BusinessType.Retail);

        var branch = tenant.AddBranch("Main", "main", "L1", null, "City", "Province", null);
        var user = tenant.AddUser("owner@example.com", "hash", "Ace", "Agas", UserRole.Owner);

        tenant.Branches.Should().ContainSingle().Which.Should().Be(branch);
        tenant.Users.Should().ContainSingle().Which.Should().Be(user);
        branch.TenantId.Should().Be(tenant.Id);
        user.TenantId.Should().Be(tenant.Id);
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
        var user = User.Create(Guid.NewGuid(), "  OWNER@Example.COM ", "hash", "Ace", "Agas", UserRole.Owner);

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
