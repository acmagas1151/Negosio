using Microsoft.EntityFrameworkCore;
using Negosio.Application.Pos;
using Negosio.Domain.Entities;
using Negosio.Domain.Enums;
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

        // DeliveryReceiptItem.SaleItemId is a real FK to SaleItems, so this round-trip needs two
        // genuine SaleItem rows to point at. The receipt itself stays deliberately sale-less
        // (saleId: null) — the FK constrains the item's SaleItemId only, never the receipt's own
        // SaleId — so any completed sale's items serve the purpose here.
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, cokeVariantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Coke 1.5L", sku: "SKU-COKE", sellingPrice: 75m);
        var (_, pandesalVariantId) = await SeedStockedProductAsync(
            branchId, category.Id, name: "Pandesal", sku: "SKU-PANDESAL", sellingPrice: 25m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId,
            session.Id,
            Guid.NewGuid(),
            new[]
            {
                new CheckoutItemInput(cokeVariantId, 2m, null),
                new CheckoutItemInput(pandesalVariantId, 3m, null)
            },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 500m) }));

        // SaleResultDto does not expose its line items, so read the real SaleItem ids back. Matched by
        // product name rather than by CreatedAtUtc order: both rows are written in one checkout and can
        // share a timestamp, which would make an ordered pick non-deterministic.
        var saleItems = await InScopeAsync(db => db.SaleItems
            .AsNoTracking()
            .Where(i => i.SaleId == sale.SaleId)
            .Select(i => new { i.Id, i.ProductNameSnapshot })
            .ToListAsync());

        var cokeSaleItemId = saleItems.Single(i => i.ProductNameSnapshot == "Coke 1.5L").Id;
        var pandesalSaleItemId = saleItems.Single(i => i.ProductNameSnapshot == "Pandesal").Id;

        var receiptId = await InScopeAsync(async db =>
        {
            var receipt = DeliveryReceipt.CreateDelivery(
                tenantId,
                branchId,
                saleId: null,
                relatedSaleNumber: "S-0001",
                sequenceNumber: 1,
                scheduledDate: new DateOnly(2026, 9, 15),
                recipientName: "Juan Dela Cruz",
                deliveryAddress: "123 Rizal St, Odiongan, Romblon",
                contactNumber: "09171234567",
                notes: "Leave with the guard.",
                preparedByUserId: userId,
                preparedByNameSnapshot: "Ace Agas");

            receipt.AddItem(cokeSaleItemId, "Coke 1.5L", null, 2m, 75m);
            receipt.AddItem(pandesalSaleItemId, "Pandesal", "Dozen", 3m, null);

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
