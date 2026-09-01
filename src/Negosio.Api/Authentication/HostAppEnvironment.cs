using Negosio.Application.Abstractions;

namespace Negosio.Api.Authentication;

/// <summary>Bridges <see cref="IAppEnvironment"/> to the ASP.NET Core host environment.</summary>
public sealed class HostAppEnvironment : IAppEnvironment
{
    private readonly IHostEnvironment _environment;

    public HostAppEnvironment(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public bool IsProduction => _environment.IsProduction();
}
