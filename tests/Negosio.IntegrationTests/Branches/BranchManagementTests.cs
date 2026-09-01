using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Negosio.Application.Branches;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests.Branches;

public class BranchManagementTests : IntegrationTest
{
    public BranchManagementTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task Owner_creates_a_branch_and_can_read_it_back()
    {
        await RegisterLoginAndAuthorizeAsync();

        var created = await CreateBranchAsync("BGC Hub", "BGC");
        created.IsActive.Should().BeTrue();
        created.Code.Should().Be("BGC");

        var list = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);
        list.Should().Contain(b => b.Code == "BGC" && b.Name == "BGC Hub");

        var detail = await Client.GetFromJsonAsync<BranchDto>($"/api/branches/{created.Id}", TestJson.Options);
        detail!.City.Should().Be("Taguig");
    }

    [Fact]
    public async Task Duplicate_code_is_a_clean_conflict()
    {
        await RegisterLoginAndAuthorizeAsync();
        await CreateBranchAsync("First", "DUP");

        var second = await Client.PostAsJsonAsync("/api/branches",
            new CreateBranchRequest("Second", "dup", "L1", null, "City", "Province", null));

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await second.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("DUPLICATE_BRANCH_CODE");
    }

    [Theory]
    [InlineData(UserRole.Manager)]
    [InlineData(UserRole.Cashier)]
    [InlineData(UserRole.InventoryStaff)]
    [InlineData(UserRole.Viewer)]
    public async Task Non_owner_admin_roles_cannot_create_a_branch(UserRole role)
    {
        await RegisterLoginAndAuthorizeAsync();
        var token = await AddTenantUserTokenAsync($"{role}@example.com", role);
        Authorize(token);

        var response = await Client.PostAsJsonAsync("/api/branches",
            new CreateBranchRequest("Nope", "NOPE", "L1", null, "City", "Province", null));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Owner_and_Admin_may_create_a_branch()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var adminToken = await AddTenantUserTokenAsync("admin@example.com", UserRole.Admin);
        Authorize(adminToken);

        var response = await Client.PostAsJsonAsync("/api/branches",
            new CreateBranchRequest("Admin Branch", "ADM", "L1", null, "City", "Province", null));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_tenant_cannot_touch_another_tenants_branch()
    {
        var tenantA = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "A Co", email: "a@example.com", branchCode: "AAA"));
        var tenantB = await RegisterAndLoginAsync(NewRegisterRequest(businessName: "B Co", email: "b@example.com", branchCode: "BBB"));

        Authorize(tenantA.AccessToken);
        var aBranch = await CreateBranchAsync("A Second", "ASEC");

        Authorize(tenantB.AccessToken);
        (await Client.GetAsync($"/api/branches/{aBranch.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PutAsJsonAsync($"/api/branches/{aBranch.Id}",
            new UpdateBranchRequest("Hax", "L1", null, "C", "P", null))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PostAsync($"/api/branches/{aBranch.Id}/deactivate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Client.PostAsync($"/api/branches/{aBranch.Id}/reactivate", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_changes_name_and_address_but_not_code()
    {
        await RegisterLoginAndAuthorizeAsync();
        var branch = await CreateBranchAsync("Old Name", "KEEP");

        var response = await Client.PutAsJsonAsync($"/api/branches/{branch.Id}",
            new UpdateBranchRequest("New Name", "New L1", "Suite 2", "Makati", "Metro Manila", "1200"));
        response.EnsureSuccessStatusCode();

        var detail = await Client.GetFromJsonAsync<BranchDto>($"/api/branches/{branch.Id}", TestJson.Options);
        detail!.Name.Should().Be("New Name");
        detail.AddressLine1.Should().Be("New L1");
        detail.Code.Should().Be("KEEP");
    }

    [Fact]
    public async Task Deactivating_one_of_two_keeps_it_queryable_with_includeInactive()
    {
        await RegisterLoginAndAuthorizeAsync();
        var second = await CreateBranchAsync("Second", "SEC");

        (await Client.PostAsync($"/api/branches/{second.Id}/deactivate", null)).EnsureSuccessStatusCode();

        var activeOnly = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches", TestJson.Options);
        activeOnly.Should().NotContain(b => b.Id == second.Id);

        var all = await Client.GetFromJsonAsync<List<BranchDto>>("/api/branches?includeInactive=true", TestJson.Options);
        all.Should().Contain(b => b.Id == second.Id && !b.IsActive);
    }

    [Fact]
    public async Task The_last_active_branch_cannot_be_deactivated()
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var mainId = await GetMainBranchIdAsync(owner);

        var response = await Client.PostAsync($"/api/branches/{mainId}/deactivate", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ApiErrorBody>())!.Code.Should().Be("LAST_ACTIVE_BRANCH");
    }

    [Fact]
    public async Task Reactivate_restores_active()
    {
        await RegisterLoginAndAuthorizeAsync();
        var second = await CreateBranchAsync("Second", "SEC");
        (await Client.PostAsync($"/api/branches/{second.Id}/deactivate", null)).EnsureSuccessStatusCode();

        var response = await Client.PostAsync($"/api/branches/{second.Id}/reactivate", null);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadFromJsonAsync<BranchDto>(TestJson.Options))!.IsActive.Should().BeTrue();
    }
}
