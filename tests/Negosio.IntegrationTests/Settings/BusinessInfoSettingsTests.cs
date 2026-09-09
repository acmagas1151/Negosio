using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Settings;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Settings;

public class BusinessInfoSettingsTests : IntegrationTest
{
    public BusinessInfoSettingsTests(NegosioApiFactory factory) : base(factory) { }

    [Fact]
    public async Task Owner_sets_and_reads_business_contact_and_tin()
    {
        await RegisterLoginAndAuthorizeAsync();

        var put = await Client.PutAsJsonAsync("/api/settings/business-info",
            new UpdateBusinessInfoRequest("  0917 000 1234 ", "  123-456-789-000 "));
        put.EnsureSuccessStatusCode();

        var got = await Client.GetFromJsonAsync<BusinessInfoDto>("/api/settings/business-info", TestJson.Options);
        got!.ContactNumber.Should().Be("0917 000 1234");
        got.TaxId.Should().Be("123-456-789-000");
        got.BusinessName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Cashier_cannot_update_business_info()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        Authorize(await AddTenantUserTokenAsync("cash@example.com", UserRole.Cashier, branchId));
        (await Client.PutAsJsonAsync("/api/settings/business-info",
            new UpdateBusinessInfoRequest("x", "y"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Oversized_tax_id_is_rejected()
    {
        await RegisterLoginAndAuthorizeAsync();
        var res = await Client.PutAsJsonAsync("/api/settings/business-info",
            new UpdateBusinessInfoRequest(null, new string('9', 41)));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
