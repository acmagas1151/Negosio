using FluentAssertions;
using Negosio.Domain.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
using Xunit;

namespace Negosio.UnitTests.Settings;

public class ReceiptSettingsEntityTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    [Fact]
    public void CreateDefault_matches_hardcoded_default()
    {
        var row = ReceiptSettings.CreateDefault(Tenant, null, User);
        row.TenantId.Should().Be(Tenant);
        row.BranchId.Should().BeNull();
        row.UpdatedByUserId.Should().Be(User);
        row.ToValues().Should().Be(ReceiptSettingsValues.HardcodedDefault);
    }

    [Fact]
    public void Update_overwrites_all_fields_and_restamps_user()
    {
        var row = ReceiptSettings.CreateDefault(Tenant, null, User);
        var editor = Guid.NewGuid();
        var v = ReceiptSettingsValues.HardcodedDefault with
        {
            Width = ReceiptWidth.Mm58, SalesHeaderText = "VERIFY CO", SalesShowCashier = false
        };

        row.Update(v, editor);

        row.ToValues().Should().Be(v);
        row.UpdatedByUserId.Should().Be(editor);
    }

    [Fact]
    public void CreateFrom_seeds_from_supplied_values()
    {
        var branch = Guid.NewGuid();
        var seed = ReceiptSettingsValues.HardcodedDefault with { SalesFooterText = "Come again" };
        var row = ReceiptSettings.CreateFrom(Tenant, branch, seed, User);
        row.BranchId.Should().Be(branch);
        row.ToValues().Should().Be(seed);
    }
}
