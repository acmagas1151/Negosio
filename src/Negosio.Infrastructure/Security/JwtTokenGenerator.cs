using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Negosio.Application.Abstractions;

namespace Negosio.Infrastructure.Security;

public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    /// <summary>Claim type carrying the tenant the token is scoped to.</summary>
    public const string TenantIdClaimType = "tenant_id";

    /// <summary>Claim type carrying the user's role name.</summary>
    public const string RoleClaimType = "role";

    private readonly JwtOptions _options;
    private readonly TimeProvider _timeProvider;

    public JwtTokenGenerator(IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public AccessToken Generate(TokenSubject subject)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresUtc = nowUtc.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(JwtRegisteredClaimNames.Email, subject.Email),
            new(TenantIdClaimType, subject.TenantId.ToString()),
            new(RoleClaimType, subject.Role.ToString())
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: nowUtc,
            expires: expiresUtc,
            signingCredentials: credentials);

        var value = new JwtSecurityTokenHandler().WriteToken(token);
        return new AccessToken(value, expiresUtc);
    }
}
