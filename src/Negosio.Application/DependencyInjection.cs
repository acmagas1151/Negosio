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
using Negosio.Application.Staff;

namespace Negosio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>(ServiceLifetime.Scoped);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IApproverVerificationService, ApproverVerificationService>();
        services.AddScoped<IDashboardService, DashboardService>();

        // Phase 2.5: platform / tenant provisioning
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();

        // Phase 2: Catalog + Inventory
        services.AddScoped<IBranchQueryService, BranchQueryService>();

        // Phase 5: Branch management & branch-scoped access
        services.AddScoped<IBranchManagementService, BranchManagementService>();
        services.AddScoped<IBranchAccessResolver, BranchAccessResolver>();
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<IProductService, ProductService>();
        services.AddScoped<IProductVariantService, ProductVariantService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IInventoryPosting, InventoryPosting>();

        // Phase 3: Retail POS
        services.AddScoped<IDocumentNumberService, DocumentNumberService>();
        services.AddScoped<IRegisterService, RegisterService>();
        services.AddScoped<IRegisterSessionService, RegisterSessionService>();
        services.AddScoped<IRegisterCashMovementService, RegisterCashMovementService>();
        services.AddScoped<IPosCatalogService, PosCatalogService>();
        services.AddScoped<IPosContextService, PosContextService>();
        services.AddScoped<ICheckoutService, CheckoutService>();
        services.AddScoped<SaleQueryService>();
        services.AddScoped<ISaleQueryService>(sp => sp.GetRequiredService<SaleQueryService>());
        services.AddScoped<IReceiptService, ReceiptService>();
        services.AddScoped<IReturnService, ReturnService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();

        // Phase 4: Staff & access management
        services.AddScoped<IStaffService, StaffService>();
        services.AddScoped<IStaffInvitationService, StaffInvitationService>();

        // Phase 6: Void sales & cash operations
        services.AddScoped<ISalesVoidPermissionService, SalesVoidPermissionService>();
        services.AddScoped<IVoidSaleService, VoidSaleService>();

        return services;
    }
}
