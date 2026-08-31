namespace Negosio.Application.Abstractions;

/// <summary>Minimal host-environment seam so the Application layer can vary dev-only behaviour
/// (e.g. surfacing a staff-invitation link when no outbound email is configured yet).</summary>
public interface IAppEnvironment
{
    bool IsDevelopment { get; }
}
