using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Auth;
using Negosio.Application.Catalog;
using Negosio.Application.Inventory;
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

    public async Task InitializeAsync() => await ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task ResetDatabaseAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Order respects FKs; cascade would also work but explicit is clearer for a test helper.
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM StockMovements; DELETE FROM BranchInventories; DELETE FROM ProductVariants; " +
            "DELETE FROM Products; DELETE FROM Categories; " +
            "DELETE FROM Users; DELETE FROM Branches; DELETE FROM Tenants;");
    }

    protected async Task<T> InScopeAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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

    // ---- Phase 2 helpers ----

    protected void Authorize(string token) =>
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    /// <summary>Register a fresh tenant, log its owner in, and set the bearer header on <see cref="Client"/>.</summary>
    protected async Task<LoginResponse> RegisterLoginAndAuthorizeAsync(RegisterRequest? request = null)
    {
        var login = await RegisterAndLoginAsync(request);
        Authorize(login.AccessToken);
        return login;
    }

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
        InScopeAsync(db => db.Branches
            .Where(b => b.TenantId == login.User.TenantId)
            .Select(b => b.Id)
            .FirstAsync());
}
