namespace Negosio.Application.Abstractions;

/// <summary>Minimal host-environment seam so the Application layer can vary non-production behaviour
/// (e.g. surfacing a staff-invitation link while no outbound email provider is configured).</summary>
public interface IAppEnvironment
{
    /// <summary>True only in the Production environment. Non-production builds may reveal a
    /// staff-invitation acceptance link in API responses / logs.</summary>
    bool IsProduction { get; }
}
