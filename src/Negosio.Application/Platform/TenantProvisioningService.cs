using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Negosio.Application.Abstractions;
using Negosio.Application.Common;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;

namespace Negosio.Application.Platform;

public sealed record ProvisionBranchInput(
    string Name, string Code, string AddressLine1, string? AddressLine2, string City, string Province, string? PostalCode);

public sealed record ProvisionOwnerInput(string FirstName, string LastName, string EmailNormalized, string PasswordHash);

public sealed record ProvisionTenantCommand(
    string BusinessName,
    BusinessType BusinessType,
    ProvisionBranchInput Branch,
    ProvisionOwnerInput Owner);

public sealed record ProvisionResult(Guid TenantId, Guid BranchId, Guid OwnerUserId);

public interface ITenantProvisioningService
{
    Task<ProvisionResult> ProvisionAsync(ProvisionTenantCommand command, CancellationToken cancellationToken = default);
}

public sealed class TenantProvisioningService : ITenantProvisioningService
{
    private readonly IPlatformDbContext _platform;
    private readonly ITenantDatabaseProvisioner _provisioner;
    private readonly ITenantDbContextFactory _tenantFactory;
    private readonly ILogger<TenantProvisioningService> _logger;

    public TenantProvisioningService(
        IPlatformDbContext platform,
        ITenantDatabaseProvisioner provisioner,
        ITenantDbContextFactory tenantFactory,
        ILogger<TenantProvisioningService> logger)
    {
        _platform = platform;
        _provisioner = provisioner;
        _tenantFactory = tenantFactory;
        _logger = logger;
    }

    public async Task<ProvisionResult> ProvisionAsync(ProvisionTenantCommand command, CancellationToken cancellationToken = default)
    {
        var email = command.Owner.EmailNormalized;

        var login = await _platform.PlatformUserLogins
            .FirstOrDefaultAsync(l => l.EmailNormalized == email, cancellationToken);

        Tenant tenant;
        TenantDatabase mapping;

        if (login is not null)
        {
            tenant = await _platform.Tenants.SingleAsync(t => t.Id == login.TenantId, cancellationToken);
            if (tenant.ProvisioningStatus is TenantProvisioningStatus.Active or TenantProvisioningStatus.Suspended)
            {
                throw new ConflictException(ErrorCodes.DuplicateEmail, "An account with this email already exists.");
            }

            mapping = await _platform.TenantDatabases.SingleAsync(d => d.TenantId == tenant.Id, cancellationToken);
        }
        else
        {
            tenant = Tenant.Create(command.BusinessName, command.BusinessType);
            login = PlatformUserLogin.Create(Guid.NewGuid(), tenant.Id, email, command.Owner.PasswordHash, UserRole.Owner);
            mapping = TenantDatabase.Create(
                tenant.Id,
                _provisioner.DatabaseNameFor(tenant.Id, command.BusinessName, command.Branch.Code),
                _provisioner.DefaultServerKey);

            await using var tx = await _platform.Database.BeginTransactionAsync(cancellationToken);
            _platform.Tenants.Add(tenant);
            _platform.PlatformUserLogins.Add(login);
            _platform.TenantDatabases.Add(mapping);
            try
            {
                await _platform.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (SqlUniqueViolation.Is(ex))
            {
                await tx.RollbackAsync(cancellationToken);
                throw new ConflictException(ErrorCodes.DuplicateEmail, "An account with this email already exists.");
            }
        }

        try
        {
            tenant.MarkProvisioning();
            await _platform.SaveChangesAsync(cancellationToken);

            var connectionString = _provisioner.BuildConnectionString(mapping.ServerKey, mapping.DatabaseName);
            await _provisioner.EnsureDatabaseAsync(mapping.ServerKey, mapping.DatabaseName, cancellationToken);

            await using var tenantDb = _tenantFactory.CreateForConnection(connectionString);
            await tenantDb.Database.MigrateAsync(cancellationToken);

            var seed = await SeedTenantAsync(tenantDb, tenant, login, command, cancellationToken);

            tenant.MarkActive();
            mapping.MarkActive();
            await _platform.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Provisioned tenant {TenantId} ({BusinessType}) database {Database}",
                tenant.Id, command.BusinessType, mapping.DatabaseName);

            return new ProvisionResult(tenant.Id, seed.BranchId, seed.OwnerUserId);
        }
        catch (Exception ex) when (ex is not AppException)
        {
            tenant.MarkFailed();
            mapping.MarkFailed();
            await _platform.SaveChangesAsync(CancellationToken.None);
            _logger.LogError(ex, "Provisioning failed for tenant {TenantId}", tenant.Id);
            throw new TenantProvisioningException("We couldn't finish setting up your business. Please try again.");
        }
    }

    private static async Task<(Guid BranchId, Guid OwnerUserId)> SeedTenantAsync(
        ITenantDbContext tenantDb, Tenant tenant, PlatformUserLogin login, ProvisionTenantCommand command, CancellationToken cancellationToken)
    {
        await using var tx = await tenantDb.Database.BeginTransactionAsync(cancellationToken);

        if (!await tenantDb.TenantProfiles.AnyAsync(cancellationToken))
        {
            tenantDb.TenantProfiles.Add(TenantProfile.Create(tenant.Id, tenant.Name, tenant.BusinessType));
        }

        var branch = await tenantDb.Branches.FirstOrDefaultAsync(cancellationToken);
        if (branch is null)
        {
            var b = command.Branch;
            branch = Branch.Create(tenant.Id, b.Name, b.Code, b.AddressLine1, b.AddressLine2, b.City, b.Province, b.PostalCode);
            tenantDb.Branches.Add(branch);
        }

        var owner = await tenantDb.Users.FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            owner = User.Create(login.Id, tenant.Id, login.EmailNormalized, command.Owner.FirstName, command.Owner.LastName, UserRole.Owner);
            tenantDb.Users.Add(owner);
        }

        await tenantDb.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return (branch.Id, owner.Id);
    }
}
