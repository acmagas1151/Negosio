using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Negosio.Application.Auth;
using Negosio.Application.Dashboard;

namespace Negosio.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<RegisterRequestValidator>(ServiceLifetime.Scoped);

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IDashboardService, DashboardService>();

        return services;
    }
}
