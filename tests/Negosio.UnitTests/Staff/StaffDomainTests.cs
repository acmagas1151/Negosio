using FluentAssertions;
using FluentValidation.TestHelper;
using Negosio.Application.Common;
using Negosio.Application.Staff;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Staff;

public class StaffInvitationTests
{
    private static StaffInvitation NewInvitation(DateTime now, UserRole role = UserRole.Cashier) =>
        StaffInvitation.Create(Guid.NewGuid(), "new@example.com", role, "hash", now.AddDays(7), Guid.NewGuid());

    private readonly DateTime _now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Cannot_create_an_Owner_invitation()
    {
        var act = () => StaffInvitation.Create(Guid.NewGuid(), "x@example.com", UserRole.Owner, "h", _now.AddDays(1), Guid.NewGuid());
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void New_invitation_is_pending_then_accept_marks_it()
    {
        var invite = NewInvitation(_now);
        invite.StatusAt(_now).Should().Be(StaffInvitationStatus.Pending);

        invite.Accept(_now);

        invite.AcceptedAtUtc.Should().Be(_now);
        invite.StatusAt(_now).Should().Be(StaffInvitationStatus.Accepted);
        invite.IsPending(_now).Should().BeFalse();
    }

    [Fact]
    public void Cannot_accept_twice()
    {
        var invite = NewInvitation(_now);
        invite.Accept(_now);

        var act = () => invite.Accept(_now.AddMinutes(1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cannot_accept_after_expiry()
    {
        var invite = NewInvitation(_now);
        var later = _now.AddDays(8);

        invite.StatusAt(later).Should().Be(StaffInvitationStatus.Expired);
        var act = () => invite.Accept(later);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cannot_accept_a_revoked_invitation()
    {
        var invite = NewInvitation(_now);
        invite.Revoke(_now);

        invite.StatusAt(_now).Should().Be(StaffInvitationStatus.Revoked);
        var act = () => invite.Accept(_now);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Cannot_revoke_an_accepted_invitation()
    {
        var invite = NewInvitation(_now);
        invite.Accept(_now);

        var act = () => invite.Revoke(_now.AddMinutes(1));
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reissue_replaces_the_token_and_pushes_expiry()
    {
        var invite = NewInvitation(_now.AddDays(-10)); // already expired
        invite.StatusAt(_now).Should().Be(StaffInvitationStatus.Expired);

        invite.Reissue("new-hash", _now.AddDays(7), _now, UserRole.Cashier, Guid.NewGuid());

        invite.TokenHash.Should().Be("new-hash");
        invite.BranchId.Should().NotBeNull();
        invite.StatusAt(_now).Should().Be(StaffInvitationStatus.Pending);
    }
}

public class StaffRoleGuardTests
{
    [Fact]
    public void User_ChangeRole_rejects_promoting_to_Owner()
    {
        var user = User.Create(Guid.NewGuid(), Guid.NewGuid(), "u@example.com", "A", "B", UserRole.Cashier);
        var act = () => user.ChangeRole(UserRole.Owner);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void User_ChangeRole_rejects_changing_an_Owner()
    {
        var owner = User.Create(Guid.NewGuid(), Guid.NewGuid(), "o@example.com", "A", "B", UserRole.Owner);
        var act = () => owner.ChangeRole(UserRole.Manager);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void User_deactivate_then_reactivate()
    {
        var user = User.Create(Guid.NewGuid(), Guid.NewGuid(), "u@example.com", "A", "B", UserRole.Cashier);
        user.IsActive.Should().BeTrue();

        user.Deactivate();
        user.IsActive.Should().BeFalse();

        user.Reactivate();
        user.IsActive.Should().BeTrue();
    }

    [Fact]
    public void PlatformLogin_ChangeRole_and_reactivate_work_within_guards()
    {
        var login = PlatformUserLogin.Create(Guid.NewGuid(), Guid.NewGuid(), "u@example.com", "hash", UserRole.Cashier);

        login.ChangeRole(UserRole.Manager);
        login.Role.Should().Be(UserRole.Manager);

        login.Deactivate();
        login.IsActive.Should().BeFalse();
        login.Reactivate();
        login.IsActive.Should().BeTrue();

        var promote = () => login.ChangeRole(UserRole.Owner);
        promote.Should().Throw<InvalidOperationException>();
    }
}

public class StaffRolesTests
{
    [Fact]
    public void Owner_can_assign_every_non_owner_role_but_not_Owner()
    {
        var assignable = StaffRoles.AssignableBy(UserRole.Owner);

        assignable.Should().NotContain(UserRole.Owner);
        assignable.Should().Contain(new[]
        {
            UserRole.Admin, UserRole.Manager, UserRole.Cashier,
            UserRole.InventoryStaff, UserRole.KitchenStaff, UserRole.Viewer
        });
    }

    [Fact]
    public void Admin_cannot_assign_Admin_or_Owner()
    {
        var assignable = StaffRoles.AssignableBy(UserRole.Admin);

        assignable.Should().NotContain(UserRole.Owner);
        assignable.Should().NotContain(UserRole.Admin);
        assignable.Should().Contain(UserRole.Manager);
        StaffRoles.CanAssign(UserRole.Admin, UserRole.Admin).Should().BeFalse();
        StaffRoles.CanAssign(UserRole.Admin, UserRole.Cashier).Should().BeTrue();
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.InventoryStaff)]
    [InlineData(UserRole.Viewer)]
    public void Non_admin_roles_cannot_assign_anything(UserRole actor)
    {
        StaffRoles.AssignableBy(actor).Should().BeEmpty();
    }
}

public class PasswordRulesTests
{
    [Theory]
    [InlineData("SecurePassword123!", true)]
    [InlineData("Shrt1!a", true)]         // meets complexity (length is a separate rule)
    [InlineData("alllowercase1!", false)] // no upper
    [InlineData("ALLUPPERCASE1!", false)] // no lower
    [InlineData("NoDigitsHere!", false)]  // no digit
    [InlineData("NoSpecial123", false)]   // no special
    [InlineData("", false)]
    public void MeetsComplexity_checks_the_four_character_classes(string password, bool expected)
    {
        PasswordRules.MeetsComplexity(password).Should().Be(expected);
    }

    [Fact]
    public void Password_rule_rejects_a_short_but_complex_password()
    {
        var validator = new AcceptInvitationRequestValidator();
        var result = validator.TestValidate(new AcceptInvitationRequest("A", "B", "Shrt1!a"));
        result.ShouldHaveValidationErrorFor(x => x.Password);
    }
}

public class InvitationTokenTests
{
    [Fact]
    public void Generate_yields_distinct_raw_tokens_with_a_matching_hash()
    {
        var a = InvitationToken.Generate();
        var b = InvitationToken.Generate();

        a.Raw.Should().NotBe(b.Raw);
        a.Hash.Should().NotBe(b.Hash);
        InvitationToken.Hash(a.Raw).Should().Be(a.Hash);
        a.Hash.Should().MatchRegex("^[0-9a-f]{64}$"); // SHA-256 hex
    }
}
