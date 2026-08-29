using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Negosio.Domain.Entities;

namespace Negosio.Application.Abstractions;

/// <summary>
/// Persistence seam for the Application layer. Intentionally exposes concrete <see cref="DbSet{T}"/>s
/// (rather than a generic repository) plus the transaction/save primitives the use cases need.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<Tenant> Tenants { get; }

    DbSet<Branch> Branches { get; }

    DbSet<User> Users { get; }

    DatabaseFacade Database { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
