using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Negosio.Api.Contracts;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Infrastructure.Security;

namespace Negosio.Api.Authentication;

/// <summary>
/// Configures the JWT bearer scheme from <see cref="JwtOptions"/> resolved through DI, so the final
/// (post-configuration-override) values are used. Also renders auth failures as the API error envelope.
/// </summary>
public sealed class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly JwtOptions _jwt;

    public ConfigureJwtBearerOptions(IOptions<JwtOptions> jwt)
    {
        _jwt = jwt.Value;
    }

    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name is not JwtBearerDefaults.AuthenticationScheme)
        {
            return;
        }

        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = _jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwt.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = JwtTokenGenerator.RoleClaimType
        };

        options.Events = new JwtBearerEvents
        {
            // Defence against a stale token: after the signature/lifetime pass, confirm the account
            // still exists, is still active, and still holds the role baked into the token. This is
            // one indexed lookup on the small platform database per authenticated request. It makes
            // deactivation effective within one request and forces a re-login after a role change,
            // without a token blacklist or a shorter global lifetime.
            OnTokenValidated = ValidateAccountStateAsync,

            OnChallenge = async context =>
            {
                context.HandleResponse();
                await WriteErrorAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    ErrorCodes.Unauthorized,
                    "Authentication is required to access this resource.");
            },
            OnForbidden = context => WriteErrorAsync(
                context.HttpContext,
                StatusCodes.Status403Forbidden,
                ErrorCodes.Forbidden,
                "You do not have permission to access this resource.")
        };
    }

    private static async Task ValidateAccountStateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        var sub = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);
        var tokenRole = principal?.FindFirstValue(JwtTokenGenerator.RoleClaimType);

        if (!Guid.TryParse(sub, out var userId) || string.IsNullOrEmpty(tokenRole))
        {
            context.Fail("The token is missing a required claim.");
            return;
        }

        var platform = context.HttpContext.RequestServices.GetRequiredService<IPlatformDbContext>();
        var login = await platform.PlatformUserLogins.AsNoTracking()
            .Where(l => l.Id == userId)
            .Select(l => new { l.IsActive, l.Role })
            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

        if (login is null || !login.IsActive)
        {
            context.Fail("This account is no longer active.");
            return;
        }

        if (!string.Equals(tokenRole, login.Role.ToString(), StringComparison.Ordinal))
        {
            context.Fail("Your access has changed. Please sign in again.");
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var error = new ApiError
        {
            Code = code,
            Message = message,
            TraceId = Activity.Current?.Id ?? context.TraceIdentifier
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(error, SerializerOptions));
    }
}
