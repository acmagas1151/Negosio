using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Auth;
using Negosio.Application.Catalog;
using Negosio.Application.Dashboard;
using Negosio.Application.Inventory;

namespace Negosio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>(ServiceLifetime.Scoped);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDashboardService, DashboardService>();

        // Phase 2: Catalog + Inventory
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IProductVariantService, ProductVariantService>();
        services.AddScoped<IInventoryService, InventoryService>();

        return services;
    }
}
