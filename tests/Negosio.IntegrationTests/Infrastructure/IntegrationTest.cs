using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Auth;
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
}
