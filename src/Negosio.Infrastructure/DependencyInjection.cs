using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Abstractions;
using Negosio.Infrastructure.Persistence;
using Negosio.Infrastructure.Security;
using Negosio.Infrastructure.Tenancy;

namespace Negosio.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- Platform (control-plane) database: one fixed connection ----
        services.AddDbContext<PlatformDbContext>((sp, options) =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("Connection string 'Platform' is not configured.");
            }

            options.UseSqlServer(connectionString, sql => sql
                .MigrationsAssembly(typeof(PlatformDbContext).Assembly.FullName)
                .MigrationsHistoryTable("__PlatformMigrationsHistory"));
        });
        services.AddScoped<IPlatformDbContext>(sp => sp.GetRequiredService<PlatformDbContext>());

        // ---- Tenant routing ----
        services.AddMemoryCache();
        services.AddSingleton<TenantServerRegistry>();
        services.AddScoped<TenantConnectionAccessor>();
        services.AddScoped<ITenantConnectionResolver, TenantConnectionResolver>();
        services.AddSingleton<ITenantDatabaseProvisioner, SqlDatabaseProvisioner>();
        services.AddScoped<ITenantDbContextFactory, TenantDbContextFactory>();

        // ---- Tenant (operational) database: resolved per request from the authenticated tenant ----
        services.AddDbContext<TenantDbContext>((sp, options) =>
        {
            var connection = sp.GetRequiredService<TenantConnectionAccessor>().Require();
            options.UseSqlServer(connection.ConnectionString, sql => sql
                .MigrationsAssembly(typeof(TenantDbContext).Assembly.FullName));
        });
        services.AddScoped<ITenantDbContext>(sp => sp.GetRequiredService<TenantDbContext>());

        // ---- Security (unchanged) ----
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingletonTimeProvider();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
