using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Auth;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;

namespace Negosio.IntegrationTests;

public class RegistrationTests : IntegrationTest
{
    public RegistrationTests(NegosioApiFactory factory) : base(factory)
    {
    }

    [Theory]
    [InlineData("Retail")]
    [InlineData("FoodAndBeverage")]
    public async Task Registration_succeeds_for_supported_business_types(string businessType)
    {
        var request = NewRegisterRequest(businessType: businessType, email: $"{businessType}@example.com");

        var response = await Client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<RegisterResponseBody>();
        body!.TenantId.Should().NotBeEmpty();
        body.BranchId.Should().NotBeEmpty();
        body.OwnerUserId.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Registration_rejects_diagnostic_center()
    {
        var request = NewRegisterRequest(businessType: "DiagnosticCenter", email: "lab@example.com");

        var response = await Client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("BUSINESS_TYPE_NOT_AVAILABLE");
        error.Message.Should().Contain("coming soon");

        var tenantCount = await InPlatformScopeAsync(db => db.Tenants.CountAsync());
        tenantCount.Should().Be(0);
    }

    [Fact]
    public async Task Registration_creates_tenant_branch_and_owner_with_owner_role()
    {
        var request = NewRegisterRequest(businessType: "Retail");

        var response = await Client.PostAsJsonAsync("/api/auth/register", request);
        response.EnsureSuccessStatusCode();
        var body = (await response.Content.ReadFromJsonAsync<RegisterResponseBody>())!;

        var tenantId = await InPlatformScopeAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync();
            tenant.Id.Should().Be(body.TenantId);
            tenant.Name.Should().Be("Bruno's Cafe");
            tenant.BusinessType.Should().Be(BusinessType.Retail);
            tenant.IsActive.Should().BeTrue();
            tenant.ProvisioningStatus.Should().Be(TenantProvisioningStatus.Active);

            // The password hash lives only in the platform login directory, never in the tenant DB.
            var login = await db.PlatformUserLogins.SingleAsync();
            login.TenantId.Should().Be(tenant.Id);
            login.EmailNormalized.Should().Be("owner@example.com");
            login.Role.Should().Be(UserRole.Owner);
            login.PasswordHash.Should().NotBeNullOrWhiteSpace();
            login.PasswordHash.Should().NotContain("SecurePassword123!");
            return tenant.Id;
        });

        await InTenantScopeAsync(tenantId, async db =>
        {
            var branch = await db.Branches.SingleAsync();
            branch.TenantId.Should().Be(tenantId);
            branch.Code.Should().Be("MAIN");
            branch.City.Should().Be("Odiongan");

            var owner = await db.Users.SingleAsync();
            owner.TenantId.Should().Be(tenantId);
            owner.Email.Should().Be("owner@example.com");
            owner.Role.Should().Be(UserRole.Owner);
            return true;
        });
    }

    [Fact]
    public async Task Duplicate_email_is_rejected_with_conflict()
    {
        await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest(businessType: "Retail"));

        var duplicate = NewRegisterRequest(businessName: "Second Business", businessType: "Retail", branchCode: "HQ");
        var response = await Client.PostAsJsonAsync("/api/auth/register", duplicate);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("DUPLICATE_EMAIL");
    }

    [Fact]
    public async Task Weak_password_is_rejected_with_validation_error()
    {
        var request = NewRegisterRequest(businessType: "Retail", password: "weak");

        var response = await Client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("VALIDATION_FAILED");
        error.Errors.Should().ContainKey("owner.password");
    }

    [Fact]
    public async Task Registration_is_atomic_when_a_later_step_fails()
    {
        // Arrange: an account already owns this email.
        await Client.PostAsJsonAsync("/api/auth/register", NewRegisterRequest(businessType: "Retail"));

        // Act: a second registration reuses the email. The unique email index on the platform login
        // directory rejects it before any tenant database is created, so no partial state remains.
        var conflicting = NewRegisterRequest(businessName: "Should Roll Back", businessType: "Retail", branchCode: "RB");
        var response = await Client.PostAsJsonAsync("/api/auth/register", conflicting);

        // Assert: nothing from the failed attempt was persisted.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await InPlatformScopeAsync(async db =>
        {
            (await db.Tenants.CountAsync()).Should().Be(1);
            (await db.PlatformUserLogins.CountAsync()).Should().Be(1);
            (await db.TenantDatabases.CountAsync()).Should().Be(1);
            (await db.Tenants.AnyAsync(t => t.Name == "Should Roll Back")).Should().BeFalse();
            return true;
        });
    }

    [Fact]
    public async Task Unknown_business_type_is_rejected_with_validation_error()
    {
        var request = NewRegisterRequest(businessType: "Spaceport");

        var response = await Client.PostAsJsonAsync("/api/auth/register", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<ApiErrorBody>();
        error!.Code.Should().Be("VALIDATION_FAILED");
    }
}
