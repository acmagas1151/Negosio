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

public interface ITenantSettingsService
{
    Task<TaxSettingsDto> GetTaxAsync(CancellationToken cancellationToken = default);

    Task<TaxSettingsDto> UpdateTaxAsync(UpdateTaxSettingsRequest request, CancellationToken cancellationToken = default);
}

public sealed class TenantSettingsService : ITenantSettingsService
{
    private readonly ITenantDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IValidator<UpdateTaxSettingsRequest> _validator;

    public TenantSettingsService(ITenantDbContext db, ICurrentUser currentUser, IValidator<UpdateTaxSettingsRequest> validator)
    {
        _db = db;
        _currentUser = currentUser;
        _validator = validator;
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
        await _validator.ValidateAndThrowAppAsync(request, cancellationToken);

        var profile = await _db.TenantProfiles.SingleAsync(p => p.Id == tenantId, cancellationToken);
        profile.ConfigureTax(request.TaxRatePercent, request.PricesIncludeTax);
        await _db.SaveChangesAsync(cancellationToken);

        return new TaxSettingsDto(profile.TaxRatePercent, profile.PricesIncludeTax);
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
