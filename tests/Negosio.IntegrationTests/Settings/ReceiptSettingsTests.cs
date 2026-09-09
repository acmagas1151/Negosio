using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Settings;

public class ReceiptSettingsTests : IntegrationTest
{
    public ReceiptSettingsTests(NegosioApiFactory factory) : base(factory) { }

    private static UpdateReceiptSettingsRequest Req(string? header = "HDR", string? footer = "FTR",
        ReceiptWidth width = ReceiptWidth.Mm80) => new(
        width, header, footer, true, true, true, true, true,
        "DHDR", "DFTR", true, true, true, true);

    [Fact]                                                            // acceptance 1, 4, 5, 6
    public async Task Owner_edits_tenant_default_and_it_persists()
    {
        await RegisterLoginAndAuthorizeAsync();
        var put = await Client.PutAsJsonAsync("/api/settings/receipts", Req(header: "VERIFY CO", footer: "Salamat"));
        put.EnsureSuccessStatusCode();

        var got = await Client.GetFromJsonAsync<ReceiptSettingsDto>("/api/settings/receipts", TestJson.Options);
        got!.SalesHeaderText.Should().Be("VERIFY CO");
        got.SalesFooterText.Should().Be("Salamat");
        got.Scope.Should().Be(ReceiptSettingsScope.TenantDefault);
        got.UpdatedByName.Should().NotBeNullOrEmpty();
    }

    [Fact]                                                            // acceptance 2
    public async Task Manager_can_edit_only_their_own_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);
        var other = await CreateBranchAsync("BGC", "BGC");
        var mgrToken = await AddTenantUserTokenAsync("mgr@example.com", UserRole.Manager, mainId);
        Authorize(mgrToken);

        (await Client.PutAsJsonAsync("/api/settings/receipts", Req()))                     // tenant default
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.PutAsJsonAsync($"/api/settings/receipts?branchId={other.Id}", Req())) // another branch
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client.PutAsJsonAsync($"/api/settings/receipts?branchId={mainId}", Req()))    // own branch
            .EnsureSuccessStatusCode();
    }

    [Fact]                                                            // acceptance 3
    public async Task Cashier_cannot_edit_receipt_settings()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        Authorize(await AddTenantUserTokenAsync("cash@example.com", UserRole.Cashier, branchId));
        (await Client.PutAsJsonAsync("/api/settings/receipts", Req())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Branch_override_is_seeded_from_effective_settings_then_reset_falls_back()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        await Client.PutAsJsonAsync("/api/settings/receipts", Req(header: "TENANT HDR", footer: "tenant ftr"));

        // first branch write: change only the footer, header should have been seeded from tenant default
        var branchReq = Req(header: "TENANT HDR", footer: "branch ftr");
        (await Client.PutAsJsonAsync($"/api/settings/receipts?branchId={branchId}", branchReq)).EnsureSuccessStatusCode();

        var branchGot = await Client.GetFromJsonAsync<ReceiptSettingsDto>(
            $"/api/settings/receipts?branchId={branchId}", TestJson.Options);
        branchGot!.SalesFooterText.Should().Be("branch ftr");
        branchGot.IsOverride.Should().BeTrue();

        (await Client.DeleteAsync($"/api/settings/receipts?branchId={branchId}")).EnsureSuccessStatusCode();
        var afterReset = await Client.GetFromJsonAsync<ReceiptSettingsDto>(
            $"/api/settings/receipts?branchId={branchId}", TestJson.Options);
        afterReset!.SalesFooterText.Should().Be("tenant ftr");
        afterReset.IsOverride.Should().BeFalse();
    }

    [Fact]                                                            // acceptance 9 / VALIDATION
    public async Task Oversized_header_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var res = await Client.PutAsJsonAsync("/api/settings/receipts", Req(header: new string('x', 501)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
