using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Application.Auth;
using Negosio.Application.Catalog;
using Negosio.Application.Inventory;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Infrastructure.Persistence;

namespace Negosio.IntegrationTests.Infrastructure;

[Collection(ApiCollection.Name)]
public abstract class IntegrationTest : IAsyncLifetime
{
    protected IntegrationTest(NegosioApiFactory factory)
    {
        Factory = factory;
        Client = factory.CreateClient();
    }

    protected NegosioApiFactory Factory { get; }

    protected HttpClient Client { get; }

    /// <summary>Tenant id of the token currently on <see cref="Client"/> (set by <see cref="Authorize"/>).</summary>
    protected Guid CurrentTenantId { get; private set; }

    public async Task InitializeAsync() => await ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Clears the platform database and drops the tenant databases provisioned by earlier tests.
    /// Only databases this test run created are dropped (tracked in this run's platform DB) — never a
    /// blind <c>sys.databases</c> scan, which on a shared LocalDB instance would also match and drop a
    /// developer's real tenant databases.
    /// </summary>
    protected async Task ResetDatabaseAsync()
    {
        await Factory.DropProvisionedTenantDatabasesAsync();

        using var scope = Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await platform.Database.ExecuteSqlRawAsync(
            "DELETE FROM StaffInvitations; DELETE FROM PlatformUserLogins; DELETE FROM TenantDatabases; DELETE FROM Tenants;");
    }

    /// <summary>Run against the current tenant's operational database.</summary>
    protected Task<T> InScopeAsync<T>(Func<TenantDbContext, Task<T>> action) =>
        InTenantScopeAsync(CurrentTenantId, action);

    protected async Task<T> InTenantScopeAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<ITenantDbContextFactory>();
        await using var db = (TenantDbContext)await factory.CreateAsync(tenantId);
        return await action(db);
    }

    protected async Task<T> InPlatformScopeAsync<T>(Func<PlatformDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await action(db);
    }

    protected static RegisterRequest NewRegisterRequest(
        string businessName = "Bruno's Cafe",
        string businessType = "FoodAndBeverage",
        string branchCode = "MAIN",
        string email = "owner@example.com",
        string password = "SecurePassword123!") => new(
        businessName,
        businessType,
        new RegisterBranchInput("Main Branch", branchCode, "Odiongan", null, "Odiongan", "Romblon", "5505"),
        new RegisterOwnerInput("Ace", "Agas", email, password));

    protected async Task<LoginResponse> RegisterAndLoginAsync(RegisterRequest? request = null)
    {
        request ??= NewRegisterRequest();

        var registerResponse = await Client.PostAsJsonAsync("/api/auth/register", request);
        registerResponse.EnsureSuccessStatusCode();

        var loginResponse = await Client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest(request.Owner.Email, request.Owner.Password));
        loginResponse.EnsureSuccessStatusCode();

        return (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(TestJson.Options))!;
    }

    protected void Authorize(string token)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Best-effort: negative-path tests deliberately pass malformed/tampered tokens.
        var handler = new JwtSecurityTokenHandler();
        if (handler.CanReadToken(token)
            && handler.ReadJwtToken(token).Claims.FirstOrDefault(c => c.Type == "tenant_id") is { } claim
            && Guid.TryParse(claim.Value, out var tenantId))
        {
            CurrentTenantId = tenantId;
        }
    }

    protected async Task<LoginResponse> RegisterLoginAndAuthorizeAsync(RegisterRequest? request = null)
    {
        var login = await RegisterAndLoginAsync(request);
        Authorize(login.AccessToken);
        return login;
    }

    /// <summary>
    /// Create an extra user in the current tenant — both the tenant profile and the platform login
    /// (so the per-request account-state check in ConfigureJwtBearerOptions passes) — and mint a token.
    /// </summary>
    protected async Task<string> AddTenantUserTokenAsync(
        string email, Negosio.Domain.Enums.UserRole role, Guid? branchId = null)
    {
        var userId = Guid.NewGuid();

        await InScopeAsync(async db =>
        {
            // Branch-scoped roles must have an active branch (Phase 5). Default to the tenant's first.
            var resolvedBranchId = branchId;
            if (resolvedBranchId is null && role is not (Negosio.Domain.Enums.UserRole.Owner or Negosio.Domain.Enums.UserRole.Admin))
            {
                resolvedBranchId = await db.Branches.Select(b => b.Id).FirstAsync();
            }

            db.Users.Add(Negosio.Domain.Entities.User.Create(
                userId, CurrentTenantId, email, "Test", "User", role, resolvedBranchId));
            await db.SaveChangesAsync();
            return true;
        });

        await InPlatformScopeAsync(async platform =>
        {
            platform.PlatformUserLogins.Add(Negosio.Domain.Entities.PlatformUserLogin.Create(
                userId, CurrentTenantId, email, "not-a-real-hash", role));
            await platform.SaveChangesAsync();
            return true;
        });

        var generator = Factory.Services.GetRequiredService<IJwtTokenGenerator>();
        return generator.Generate(new TokenSubject(userId, CurrentTenantId, role, email)).Value;
    }

    /// <summary>
    /// Add a user who can actually log in: a real password hash on the platform login plus a tenant
    /// User (optionally branch-assigned). Returns the user id.
    /// </summary>
    protected async Task<Guid> AddLoginableTenantUserAsync(
        string email, string password, Negosio.Domain.Enums.UserRole role, Guid? branchId = null)
    {
        var userId = Guid.NewGuid();
        var hasher = Factory.Services.GetRequiredService<Negosio.Application.Abstractions.IPasswordHasher>();
        var hash = hasher.Hash(password);

        await InScopeAsync(async db =>
        {
            db.Users.Add(Negosio.Domain.Entities.User.Create(
                userId, CurrentTenantId, email, "Test", "User", role, branchId));
            await db.SaveChangesAsync();
            return true;
        });

        await InPlatformScopeAsync(async platform =>
        {
            platform.PlatformUserLogins.Add(Negosio.Domain.Entities.PlatformUserLogin.Create(
                userId, CurrentTenantId, email, hash, role));
            await platform.SaveChangesAsync();
            return true;
        });

        return userId;
    }

    protected async Task<HttpResponseMessage> RawLoginAsync(string email, string password) =>
        await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));

    protected async Task<CategoryDto> CreateCategoryAsync(string name = "Beverages", string? description = null)
    {
        var response = await Client.PostAsJsonAsync("/api/categories", new CreateCategoryRequest(name, description));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CategoryDto>(TestJson.Options))!;
    }

    protected async Task<ProductDetailDto> CreateSimpleProductAsync(
        Guid categoryId,
        string name = "Coke 1.5L",
        string? sku = null,
        string? barcode = null,
        decimal costPrice = 10m,
        decimal sellingPrice = 20m,
        bool trackInventory = true)
    {
        var request = new CreateProductRequest(
            categoryId, name, null, trackInventory, sku, barcode, costPrice, sellingPrice, Variants: null);
        var response = await Client.PostAsJsonAsync("/api/products", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProductDetailDto>(TestJson.Options))!;
    }

    protected Task<HttpResponseMessage> AdjustInventoryAsync(AdjustInventoryRequest request) =>
        Client.PostAsJsonAsync("/api/inventory/adjustments", request);

    protected async Task<InventoryRowDto> AdjustInventoryOkAsync(AdjustInventoryRequest request)
    {
        var response = await AdjustInventoryAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InventoryRowDto>(TestJson.Options))!;
    }

    protected Task<Guid> GetMainBranchIdAsync(LoginResponse login) =>
        InTenantScopeAsync(login.User.TenantId, db => db.Branches.Select(b => b.Id).FirstAsync());

    /// <summary>Create an extra branch in the current tenant via the API (Owner/Admin token required).</summary>
    protected async Task<Negosio.Application.Branches.BranchDto> CreateBranchAsync(
        string name = "BGC", string code = "BGC", string city = "Taguig", string province = "Metro Manila")
    {
        var response = await Client.PostAsJsonAsync("/api/branches",
            new Negosio.Application.Branches.CreateBranchRequest(name, code, "5th Ave", null, city, province, "1634"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Negosio.Application.Branches.BranchDto>(TestJson.Options))!;
    }

    protected async Task<RegisterDto> CreateRegisterAsync(Guid branchId, string name = "Main Counter", string code = "R1")
    {
        var response = await Client.PostAsJsonAsync("/api/registers", new CreateRegisterRequest(branchId, name, code));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterDto>(TestJson.Options))!;
    }

    protected async Task<RegisterSessionDto> OpenSessionAsync(Guid registerId, decimal openingCash = 1000m)
    {
        var response = await Client.PostAsJsonAsync("/api/register-sessions/open", new OpenRegisterSessionRequest(registerId, openingCash));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RegisterSessionDto>(TestJson.Options))!;
    }

    /// <summary>Create a simple tracked product and give it opening stock at the branch. Returns (productId, variantId).</summary>
    protected async Task<(Guid ProductId, Guid VariantId)> SeedStockedProductAsync(
        Guid branchId,
        Guid categoryId,
        string name = "Coke 1.5L",
        string sku = "SKU-1",
        decimal sellingPrice = 75m,
        decimal costPrice = 40m,
        decimal openingStock = 20m)
    {
        var product = await CreateSimpleProductAsync(categoryId, name, sku: sku, sellingPrice: sellingPrice, costPrice: costPrice);
        var variantId = product.Variants.Single().Id;

        if (openingStock > 0m)
        {
            await AdjustInventoryOkAsync(new AdjustInventoryRequest(
                branchId, product.Product.Id, null, openingStock, "Opening stock", ReorderLevel: null, ExpectedConcurrencyToken: null));
        }

        return (product.Product.Id, variantId);
    }

    protected Task<HttpResponseMessage> CheckoutAsync(CheckoutRequest request) =>
        Client.PostAsJsonAsync("/api/pos/checkout", request);

    protected async Task<SaleResultDto> CheckoutOkAsync(CheckoutRequest request)
    {
        var response = await CheckoutAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SaleResultDto>(TestJson.Options))!;
    }
}
