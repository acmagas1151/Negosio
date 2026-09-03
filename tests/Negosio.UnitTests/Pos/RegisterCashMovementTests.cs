using FluentAssertions;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.UnitTests.Pos;

public class RegisterCashMovementTests
{
    [Fact]
    public void Create_accepts_a_defined_type()
    {
        var movement = RegisterCashMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CashMovementType.CashIn, 100m, "Float top-up", Guid.NewGuid());

        movement.Type.Should().Be(CashMovementType.CashIn);
    }

    [Fact]
    public void Create_rejects_an_undefined_type()
    {
        // Defense-in-depth: FluentValidation's IsInEnum() should already catch this at the API
        // boundary, but the domain factory must never trust that alone — an out-of-range integer
        // that somehow reaches Create should never silently produce a movement that then matches
        // neither CashIn nor CashOut in the session-close reconciliation SUM queries.
        var act = () => RegisterCashMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), (CashMovementType)99, 100m, "Bogus type", Guid.NewGuid());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Create_rejects_a_non_positive_amount()
    {
        var act = () => RegisterCashMovement.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CashMovementType.CashOut, 0m, "Bad amount", Guid.NewGuid());

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
