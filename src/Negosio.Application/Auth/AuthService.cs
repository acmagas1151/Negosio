using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Auth;

public sealed class AuthService : IAuthService
{
    // Business types that registration currently accepts. DiagnosticCenter exists in the domain
    // but is intentionally excluded here so the backend enforces the rule independently of the UI.
    private static readonly HashSet<BusinessType> RegisterableBusinessTypes = new()
    {
        BusinessType.Retail,
        BusinessType.FoodAndBeverage
    };

    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _tokenGenerator;
    private readonly IValidator<RegisterRequest> _registerValidator;
    private readonly IValidator<LoginRequest> _loginValidator;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        IApplicationDbContext db,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator tokenGenerator,
        IValidator<RegisterRequest> registerValidator,
        IValidator<LoginRequest> loginValidator,
        ICurrentUser currentUser,
        ILogger<AuthService> logger)
    {
        _db = db;
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
            throw new BusinessRuleException(
                ErrorCodes.BusinessTypeNotAvailable,
                "Diagnostic Center support is coming soon.");
        }

        var normalizedEmail = User.NormalizeEmail(request.Owner.Email);
        var passwordHash = _passwordHasher.Hash(request.Owner.Password);

        // Everything below is a single unit of work: tenant, first branch and owner are created
        // together or not at all. Uniqueness is enforced by the database (no read-then-write race).
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var tenant = Tenant.Create(request.BusinessName, businessType);
        tenant.AddBranch(
            request.Branch.Name,
            request.Branch.Code,
            request.Branch.AddressLine1,
            request.Branch.AddressLine2,
            request.Branch.City,
            request.Branch.Province,
            request.Branch.PostalCode);

        var owner = tenant.AddUser(
            normalizedEmail,
            passwordHash,
            request.Owner.FirstName,
            request.Owner.LastName,
            UserRole.Owner);

        _db.Tenants.Add(tenant);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            throw TranslateUniqueViolation(ex);
        }

        _logger.LogInformation(
            "Registered tenant {TenantId} ({BusinessType}) with owner {UserId} and branch {BranchId}",
            tenant.Id, businessType, owner.Id, tenant.Branches.First().Id);

        return new RegisterResponse(tenant.Id, tenant.Branches.First().Id, owner.Id);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        await ValidateAsync(_loginValidator, request, cancellationToken);

        var normalizedEmail = User.NormalizeEmail(request.Email);

        var user = await _db.Users
            .Include(u => u.Tenant)
            .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

        // Verify a hash even when the user is unknown to keep the response time roughly constant.
        var hashToCheck = user?.PasswordHash ?? GetTimingEqualizerHash();
        var passwordValid = _passwordHasher.Verify(request.Password, hashToCheck);

        if (user is null || !passwordValid)
        {
            _logger.LogWarning("Failed login attempt for {Email}", normalizedEmail);
            throw new BusinessRuleException(ErrorCodes.InvalidCredentials, "Invalid email or password.");
        }

        if (!user.IsActive || !user.Tenant.IsActive)
        {
            throw new BusinessRuleException(ErrorCodes.AccountInactive, "This account is not active.");
        }

        var token = _tokenGenerator.Generate(user);

        _logger.LogInformation("User {UserId} of tenant {TenantId} logged in", user.Id, user.TenantId);

        return new LoginResponse(token.Value, token.ExpiresAtUtc, ToDto(user));
    }

    public async Task<AuthUserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        var user = await _db.Users
            .Include(u => u.Tenant)
            .SingleOrDefaultAsync(u => u.Id == _currentUser.UserId, cancellationToken)
            ?? throw new UnauthorizedAppException("The authenticated user no longer exists.");

        return ToDto(user);
    }

    private static AuthUserDto ToDto(User user) => new(
        user.Id,
        user.TenantId,
        user.Tenant.Name,
        user.Tenant.BusinessType,
        user.FirstName,
        user.LastName,
        user.Email,
        user.Role);

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
        // "Owner.Email" -> "owner.email"; keeps client-side field mapping predictable.
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

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // SQL Server: 2601 (unique index) / 2627 (unique constraint). Checked by number on the
        // inner exception without taking a hard dependency on Microsoft.Data.SqlClient.
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            var numberProperty = inner.GetType().GetProperty("Number");
            if (numberProperty?.GetValue(inner) is int number && number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }

    private static AppException TranslateUniqueViolation(DbUpdateException ex)
    {
        var entityTypes = ex.Entries.Select(e => e.Entity.GetType()).ToHashSet();

        if (entityTypes.Contains(typeof(User)))
        {
            return new ConflictException(ErrorCodes.DuplicateEmail, "An account with this email already exists.");
        }

        if (entityTypes.Contains(typeof(Branch)))
        {
            return new ConflictException(ErrorCodes.DuplicateBranchCode, "A branch with this code already exists.");
        }

        return new ConflictException(ErrorCodes.DuplicateEmail, "An account with this email already exists.");
    }

    // A valid-format hash of a throwaway value, computed once, used only to equalize the response
    // time of unknown-email logins so they cannot be distinguished by timing.
    private Lazy<string>? _timingEqualizerHash;

    private string GetTimingEqualizerHash() =>
        (_timingEqualizerHash ??= new Lazy<string>(() => _passwordHasher.Hash("timing-equalizer"))).Value;
}
