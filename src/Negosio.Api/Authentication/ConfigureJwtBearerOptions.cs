using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Negosio.Api.Contracts;
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
