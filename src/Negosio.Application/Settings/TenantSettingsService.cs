using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;

namespace Negosio.Application.Settings;

public sealed record TaxSettingsDto(decimal TaxRatePercent, bool PricesIncludeTax);

public sealed record UpdateTaxSettingsRequest(decimal TaxRatePercent, bool PricesIncludeTax);

public sealed class UpdateTaxSettingsRequestValidator : AbstractValidator<UpdateTaxSettingsRequest>
{
    public UpdateTaxSettingsRequestValidator()
    {
        RuleFor(x => x.TaxRatePercent)
            .InclusiveBetween(0m, 100m).WithMessage("Tax rate must be between 0 and 100.");
    }
}

public sealed record BusinessInfoDto(string BusinessName, string? ContactNumber, string? TaxId);

public sealed record UpdateBusinessInfoRequest(string? ContactNumber, string? TaxId);

public sealed class UpdateBusinessInfoRequestValidator : AbstractValidator<UpdateBusinessInfoRequest>
{
    public UpdateBusinessInfoRequestValidator()
    {
        RuleFor(x => x.ContactNumber)
            .MaximumLength(40)
            // Digits, spaces, and the punctuation a real phone number actually uses (+, -, (, )) —
            // never letters. Also requires at least 7 digits, so "+" or "()" alone doesn't pass.
            .Matches(@"^[0-9+\-()\s]+$").WithMessage("Contact number can only contain digits and + - ( ) characters.")
            .Must(n => n!.Count(char.IsDigit) >= 7).WithMessage("Contact number must contain at least 7 digits.")
            .When(x => !string.IsNullOrWhiteSpace(x.ContactNumber));
        RuleFor(x => x.TaxId)
            .MaximumLength(40);
    }
}

public interface ITenantSettingsService
{
    Task<TaxSettingsDto> GetTaxAsync(CancellationToken cancellationToken = default);

    Task<TaxSettingsDto> UpdateTaxAsync(UpdateTaxSettingsRequest request, CancellationToken cancellationToken = default);

    Task<BusinessInfoDto> GetBusinessInfoAsync(CancellationToken cancellationToken = default);

    Task<BusinessInfoDto> UpdateBusinessInfoAsync(UpdateBusinessInfoRequest request, CancellationToken cancellationToken = default);
}

public sealed class TenantSettingsService : ITenantSettingsService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<UpdateTaxSettingsRequest> _taxValidator;
    private readonly IValidator<UpdateBusinessInfoRequest> _businessInfoValidator;

    public TenantSettingsService(ITenantDbContext db, ICurrentUser currentUser, IValidator<UpdateTaxSettingsRequest> taxValidator, IValidator<UpdateBusinessInfoRequest> businessInfoValidator)
    {
        _db = db;
        _currentUser = currentUser;
        _taxValidator = taxValidator;
        _businessInfoValidator = businessInfoValidator;
    }

    public async Task<TaxSettingsDto> GetTaxAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var profile = await _db.TenantProfiles.AsNoTracking().SingleAsync(p => p.Id == tenantId, cancellationToken);
        return new TaxSettingsDto(profile.TaxRatePercent, profile.PricesIncludeTax);
    }

    public async Task<TaxSettingsDto> UpdateTaxAsync(UpdateTaxSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _taxValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var profile = await _db.TenantProfiles.SingleAsync(p => p.Id == tenantId, cancellationToken);
        profile.ConfigureTax(request.TaxRatePercent, request.PricesIncludeTax);
        await _db.SaveChangesAsync(cancellationToken);

        return new TaxSettingsDto(profile.TaxRatePercent, profile.PricesIncludeTax);
    }

    public async Task<BusinessInfoDto> GetBusinessInfoAsync(CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        var profile = await _db.TenantProfiles.AsNoTracking().SingleAsync(p => p.Id == tenantId, cancellationToken);
        return new BusinessInfoDto(profile.Name, profile.ContactNumber, profile.TaxId);
    }

    public async Task<BusinessInfoDto> UpdateBusinessInfoAsync(UpdateBusinessInfoRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = RequireTenant();
        await _businessInfoValidator.ValidateAndThrowAppAsync(request, cancellationToken);

        var profile = await _db.TenantProfiles.SingleAsync(p => p.Id == tenantId, cancellationToken);
        profile.ConfigureBusinessInfo(request.ContactNumber, request.TaxId);
        await _db.SaveChangesAsync(cancellationToken);

        return new BusinessInfoDto(profile.Name, profile.ContactNumber, profile.TaxId);
    }

    private Guid RequireTenant()
    {
        if (!_currentUser.IsAuthenticated)
        {
            throw new UnauthorizedAppException("Not authenticated.");
        }

        return _currentUser.TenantId;
    }
}
