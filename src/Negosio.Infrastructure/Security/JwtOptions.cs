using System.ComponentModel.DataAnnotations;

namespace Negosio.Infrastructure.Security;

/// <summary>JWT settings bound from configuration (section "Jwt"). Secrets come from the environment.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    /// <summary>Symmetric signing key. Must be at least 32 bytes. Provided via configuration/environment only.</summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 1440)]
    public int AccessTokenMinutes { get; set; } = 60;
}
