using Microsoft.EntityFrameworkCore;
using Negosio.Domain.Entities;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptSchemaTests : IntegrationTest
{
    public DeliveryReceiptSchemaTests(NegosioApiFactory factory) : base(factory) { }

    [Fact]
    public async Task DeliveryReceipt_with_items_round_trips_through_the_tenant_schema()
    {
        var login = await RegisterLoginAndAuthorizeAsync();
        var tenantId = CurrentTenantId;
        var branchId = await GetMainBranchIdAsync(login);
        var userId = login.User.Id;

        var receiptId = await InScopeAsync(async db =>
        {
            var receipt = DeliveryReceipt.Create(
                tenantId,
                branchId,
                saleId: null,
                relatedSaleNumber: "S-0001",
                recipientName: "Juan Dela Cruz",
                deliveryAddress: "123 Rizal St, Odiongan, Romblon",
                contactNumber: "09171234567",
                deliveryNotes: "Leave with the guard.",
                preparedByUserId: userId,
                preparedByNameSnapshot: "Ace Agas");

            receipt.AddItem("Coke 1.5L", null, 2m, 75m);
            receipt.AddItem("Pandesal", "Dozen", 3m, null);

            db.DeliveryReceipts.Add(receipt);
            await db.SaveChangesAsync();
            return receipt.Id;
        });

        var loaded = await InScopeAsync(db => db.DeliveryReceipts
            .AsNoTracking()
            .Include(r => r.Items)
            .SingleAsync(r => r.Id == receiptId));

        Assert.Equal(2, loaded.Items.Count);
        Assert.Equal("Juan Dela Cruz", loaded.RecipientName);
        Assert.Contains(loaded.Items, i => i.ProductNameSnapshot == "Coke 1.5L" && i.Quantity == 2m && i.UnitPrice == 75m);
        Assert.Contains(loaded.Items, i => i.ProductNameSnapshot == "Pandesal" && i.VariantNameSnapshot == "Dozen" && i.UnitPrice == null);
    }
}
