using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Application.Platform;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Auth;

public sealed class AuthService : IAuthService
{
    // DiagnosticCenter exists in the domain but registration rejects it (backend-enforced, not UI-only).
    private static readonly HashSet<BusinessType> RegisterableBusinessTypes = new()
    {
        BusinessType.Retail,
        BusinessType.FoodAndBeverage
    };

    private readonly IPlatformDbContext _platform;
    private readonly ITenantDbContextFactory _tenantFactory;
    private readonly ITenantProvisioningService _provisioning;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IPlatformDbContext platform,
        ITenantDbContextFactory tenantFactory,
        ITenantProvisioningService provisioning,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator,
        ICurrentUser currentUser,
        ILogger<AuthService> logger)
    {
        _platform = platform;
        _tenantFactory = tenantFactory;
        _provisioning = provisioning;
        _passwordHasher = passwordHasher;
        _tokenGenerator = tokenGenerator;
        _registerValidator = registerValidator;
        _loginValidator = loginValidator;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<RegisterResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(_registerValidator, request, cancellationToken);

        var businessType = ParseBusinessType(request.BusinessType);
        if (!RegisterableBusinessTypes.Contains(businessType))
        {
            throw new BusinessRuleException(ErrorCodes.BusinessTypeNotAvailable, "Diagnostic Center support is coming soon.");
        }

        var normalizedEmail = User.NormalizeEmail(request.Owner.Email);
        var passwordHash = _passwordHasher.Hash(request.Owner.Password);

        var result = await _provisioning.ProvisionAsync(
            new ProvisionTenantCommand(
                request.BusinessName,
                businessType,
                new ProvisionBranchInput(
                    request.Branch.Name, request.Branch.Code, request.Branch.AddressLine1,
                    request.Branch.AddressLine2, request.Branch.City, request.Branch.Province, request.Branch.PostalCode),
                new ProvisionOwnerInput(request.Owner.FirstName, request.Owner.LastName, normalizedEmail, passwordHash)),
            cancellationToken);

        return new RegisterResponse(result.TenantId, result.BranchId, result.OwnerUserId);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(_loginValidator, request, cancellationToken);

        var normalizedEmail = User.NormalizeEmail(request.Email);

        var login = await _platform.PlatformUserLogins
            .SingleOrDefaultAsync(l => l.EmailNormalized == normalizedEmail, cancellationToken);

        // Verify a hash even when the email is unknown to keep the response time roughly constant.
        var hashToCheck = login?.PasswordHash ?? GetTimingEqualizerHash();
        var passwordValid = _passwordHasher.Verify(request.Password, hashToCheck);

        if (login is null || !passwordValid)
        {
            _logger.LogWarning("Failed login attempt for {Email}", normalizedEmail);
            throw new BusinessRuleException(ErrorCodes.InvalidCredentials, "Invalid email or password.");
        }

        var tenant = await _platform.Tenants.SingleOrDefaultAsync(t => t.Id == login.TenantId, cancellationToken);
        if (!login.IsActive || tenant is null || !tenant.IsOperational)
        {
            throw new BusinessRuleException(ErrorCodes.AccountInactive, "This account is not active.");
        }

        var token = _tokenGenerator.Generate(new TokenSubject(login.Id, login.TenantId, login.Role, login.EmailNormalized));

        await using var tenantDb = await _tenantFactory.CreateAsync(login.TenantId, cancellationToken);
        var dto = await BuildAuthUserDtoAsync(tenantDb, login.Id, cancellationToken);

        _logger.LogInformation("User {UserId} of tenant {TenantId} logged in", login.Id, login.TenantId);

        return new LoginResponse(token.Value, token.ExpiresAtUtc, dto);
    }

    public async Task<AuthUserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        await using var tenantDb = await _tenantFactory.CreateAsync(_currentUser.TenantId, cancellationToken);
        return await BuildAuthUserDtoAsync(tenantDb, _currentUser.UserId, cancellationToken);
    }

    private static async Task<AuthUserDto> BuildAuthUserDtoAsync(ITenantDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var row = await (
            from u in db.Users.AsNoTracking().Where(u => u.Id == userId)
            from p in db.TenantProfiles.AsNoTracking()
            select new { u, p })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new UnauthorizedAppException("The authenticated user no longer exists.");

        return new AuthUserDto(
            row.u.Id, row.u.TenantId, row.p.Name, row.p.BusinessType,
            row.u.FirstName, row.u.LastName, row.u.Email, row.u.Role);
    }

    private static BusinessType ParseBusinessType(string value)
    {
        if (!Enum.TryParse<BusinessType>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new ValidationAppException(new Dictionary<string, string[]>
            {
                ["businessType"] = new[] { $"'{value}' is not a supported business type." }
            });
        }

        return parsed;
    }

    private static async Task ValidateAsync<T>(IValidator<T> validator, T instance, CancellationToken cancellationToken)
    {
        var result = await validator.ValidateAsync(instance, cancellationToken);
        if (result.IsValid)
        {
            return;
        }

        var errors = result.Errors
            .GroupBy(e => ToCamelCase(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        throw new ValidationAppException(errors);
    }

    private static string ToCamelCase(string propertyName)
    {
        var segments = propertyName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i].Length > 0)
            {
                segments[i] = char.ToLowerInvariant(segments[i][0]) + segments[i][1..];
            }
        }

        return string.Join('.', segments);
    }

    private Lazy<string>? _timingEqualizerHash;

    private string GetTimingEqualizerHash() =>
        (_timingEqualizerHash ??= new Lazy<string>(() => _passwordHasher.Hash("timing-equalizer"))).Value;
}
