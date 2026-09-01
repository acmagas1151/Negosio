using FluentAssertions;
using Negosio.Application.Branches;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Branches;

public class BranchRolesTests
{
    [Theory]
    [InlineData(UserRole.Owner, true)]
    [InlineData(UserRole.Admin, true)]
    [InlineData(UserRole.Manager, false)]
    [InlineData(UserRole.Cashier, false)]
    [InlineData(UserRole.InventoryStaff, false)]
    [InlineData(UserRole.KitchenStaff, false)]
    [InlineData(UserRole.Viewer, false)]
    public void IsAllBranch_is_true_only_for_Owner_and_Admin(UserRole role, bool expected)
        => BranchRoles.IsAllBranch(role).Should().Be(expected);

    [Fact]
    public void BranchScoped_is_every_non_owner_admin_role()
        => BranchRoles.BranchScoped.Should().BeEquivalentTo(new[]
        {
            UserRole.Manager, UserRole.Cashier, UserRole.InventoryStaff,
            UserRole.KitchenStaff, UserRole.Viewer,
        });
}
