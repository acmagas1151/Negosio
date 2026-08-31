using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Auth;
using Negosio.Application.Branches;
using Negosio.Application.Catalog;
using Negosio.Application.Common;
using Negosio.Application.Dashboard;
using Negosio.Application.Inventory;
using Negosio.Application.Platform;
using Negosio.Application.Pos;
using Negosio.Application.Registers;
using Negosio.Application.Sales;
using Negosio.Application.Settings;

namespace Negosio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>(ServiceLifetime.Scoped);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDashboardService, DashboardService>();

        // Phase 2.5: platform / tenant provisioning
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // Phase 2: Catalog + Inventory
        services.AddScoped<IBranchQueryService, BranchQueryService>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IProductVariantService, ProductVariantService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IInventoryPosting, InventoryPosting>();

        // Phase 3: Retail POS
        services.AddScoped<IDocumentNumberService, DocumentNumberService>();
        services.AddScoped<IRegisterService, RegisterService>();
        services.AddScoped<IRegisterSessionService, RegisterSessionService>();
        services.AddScoped<IPosCatalogService, PosCatalogService>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<SaleQueryService>();
        services.AddScoped<ISaleQueryService>(sp => sp.GetRequiredService<SaleQueryService>());
        services.AddScoped<IReceiptService, ReceiptService>();
        services.AddScoped<IReturnService, ReturnService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();

        return services;
    }
}
