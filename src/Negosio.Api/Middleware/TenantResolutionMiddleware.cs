using Negosio.Application.Abstractions;
using Negosio.Infrastructure.Tenancy;

namespace Negosio.Api.Middleware;

/// <summary>
/// After authentication, resolves the current request's tenant database from the <c>tenant_id</c>
/// claim and stashes it for <c>TenantDbContext</c>. Unknown / suspended / not-yet-provisioned
/// tenants fail here (via <see cref="ITenantConnectionResolver"/>) before any handler runs; the
/// thrown <c>AppException</c> is rendered by <see cref="ExceptionHandlingMiddleware"/>.
/// Anonymous requests (register, login, swagger) pass straight through.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    public TenantResolutionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenantContext,
        ITenantConnectionResolver resolver,
        TenantConnectionAccessor accessor)
    {
        if (tenantContext.HasTenant)
        {
            var connection = await resolver.ResolveAsync(tenantContext.TenantId, context.RequestAborted);
            accessor.Set(connection);
        }

        await _next(context);
    }
}
