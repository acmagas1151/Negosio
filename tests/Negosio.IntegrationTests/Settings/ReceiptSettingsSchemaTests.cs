using Microsoft.EntityFrameworkCore;
using Negosio.Domain.Entities;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Settings;

public class ReceiptSettingsSchemaTests : IntegrationTest
{
    public ReceiptSettingsSchemaTests(NegosioApiFactory factory) : base(factory) { }

    [Fact]
    public async Task Tenant_default_and_branch_rows_coexist_but_duplicate_tenant_default_is_rejected()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var tenantId = CurrentTenantId;
        var branchId = await GetMainBranchIdAsync(login);
        var userId = login.User.Id;

        // (a) a tenant-default row (BranchId = null) and a branch row can both be saved.
        await InScopeAsync(async db =>
        {
            db.ReceiptSettings.Add(ReceiptSettings.CreateDefault(tenantId, null, userId));
            db.ReceiptSettings.Add(ReceiptSettings.CreateDefault(tenantId, branchId, userId));
            await db.SaveChangesAsync();
            return true;
        });

        var saved = await InScopeAsync(db => db.ReceiptSettings.AsNoTracking().CountAsync());
        Assert.Equal(2, saved);

        // (b) a SECOND tenant-default row violates the filtered unique index UX_ReceiptSettings_TenantDefault.
        var ex = await Assert.ThrowsAnyAsync<DbUpdateException>(() => InScopeAsync(async db =>
        {
            db.ReceiptSettings.Add(ReceiptSettings.CreateDefault(tenantId, null, userId));
            await db.SaveChangesAsync();
            return true;
        }));

        Assert.IsType<Microsoft.Data.SqlClient.SqlException>(ex.InnerException);
    }
}
