using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Negosio.Application.Delivery;
using Negosio.Application.Pos;
using Negosio.Domain.Enums;
using Negosio.IntegrationTests.Infrastructure;
using Xunit;

namespace Negosio.IntegrationTests.Delivery;

public class DeliveryReceiptConcurrencyTests : IntegrationTest
{
    public DeliveryReceiptConcurrencyTests(NegosioApiFactory factory) : base(factory) { }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(8));

    // Same helper as VoidConcurrencyTests.cs — explicit per-request Authorization header so two
    // concurrent requests never race on Client.DefaultRequestHeaders.
    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string url, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record Scene(Guid SaleId, Guid SaleItemId, string Token);

    private async Task<Scene> ArrangeSaleAsync(decimal qty = 10m, decimal deliveryRequiredQuantity = 4m)
    {
        var owner = await RegisterLoginAndAuthorizeAsync();
        var branchId = await GetMainBranchIdAsync(owner);
        var register = await CreateRegisterAsync(branchId);
        var session = await OpenSessionAsync(register.Id);
        var category = await CreateCategoryAsync();
        var (_, variantId) = await SeedStockedProductAsync(branchId, category.Id, sellingPrice: 100m, openingStock: qty + 10m);

        var sale = await CheckoutOkAsync(new CheckoutRequest(
            branchId, session.Id, Guid.NewGuid(),
            new[] { new CheckoutItemInput(variantId, qty, null, deliveryRequiredQuantity) },
            new[] { new CheckoutPaymentInput(PaymentMethod.Cash, ReceivedAmount: 2000m) }));

        return new Scene(sale.SaleId, sale.Items[0].SaleItemId, owner.AccessToken);
    }

    private static CreateDeliveryReceiptRequest FullyClaimingRequest(Scene s, decimal quantity) => new(
        Today, "Juan Dela Cruz", "123 Ayala Ave, Makati", null, null,
        new[] { new CreateDeliveryReceiptItemInput(s.SaleItemId, quantity) });

    [Fact]
    public async Task Two_concurrent_creates_claiming_the_same_last_units_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);

        var summary = await Client.GetFromJsonAsync<SaleDeliverySummaryDto>(
            $"/api/sales/{scene.SaleId}/delivery-summary", TestJson.Options);
        summary!.Items[0].PendingQuantity.Should().Be(4m); // never over-allocated past what was ever delivery-required
        summary.Items[0].AvailableToScheduleQuantity.Should().Be(0m);
    }

    [Fact]
    public async Task Rescheduling_after_a_cancel_races_correctly_against_a_second_claim_of_the_released_quantity()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var firstResponse = await Client.PostAsJsonAsync(
            $"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var first = (await firstResponse.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;
        await Client.PostAsJsonAsync($"/api/delivery-receipts/{first.Id}/cancel", new CancelDeliveryReceiptRequest("Wrong address"));

        HttpRequestMessage CreateRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts", scene.Token, FullyClaimingRequest(scene, 4m));

        var results = await Task.WhenAll(Client.SendAsync(CreateRequest()), Client.SendAsync(CreateRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
    }

    [Fact]
    public async Task Two_concurrent_mark_delivered_calls_on_the_same_delivery_only_one_succeeds()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        HttpRequestMessage DeliverRequest() => AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token);

        var results = await Task.WhenAll(Client.SendAsync(DeliverRequest()), Client.SendAsync(DeliverRequest()));

        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_mark_delivered_and_cancel_on_the_same_delivery_never_both_succeed()
    {
        var scene = await ArrangeSaleAsync();
        var created = await Client.PostAsJsonAsync($"/api/sales/{scene.SaleId}/delivery-receipts", FullyClaimingRequest(scene, 4m));
        var dr = (await created.Content.ReadFromJsonAsync<DeliveryReceiptDto>(TestJson.Options))!;

        var deliverTask = Client.SendAsync(AuthorizedRequest(HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/deliver", scene.Token));
        var cancelTask = Client.SendAsync(AuthorizedRequest(
            HttpMethod.Post, $"/api/delivery-receipts/{dr.Id}/cancel", scene.Token, new CancelDeliveryReceiptRequest("Race")));

        var results = await Task.WhenAll(deliverTask, cancelTask);

        results.Count(r => r.IsSuccessStatusCode).Should().Be(1);

        var final = await Client.GetFromJsonAsync<DeliveryReceiptDto>($"/api/delivery-receipts/{dr.Id}", TestJson.Options);
        final!.Status.Should().BeOneOf(FulfillmentStatus.Completed, FulfillmentStatus.Cancelled);
    }

    // Controller addendum (Task 9 review gap): the post-lock batch-idempotency re-check — added
    // mid-Task-9 because a unique index can't coexist with a batch writing N rows sharing one
    // BatchRequestId — has no test proving it survives a genuine concurrent race until this one.
    [Fact]
    public async Task Two_concurrent_batch_creates_with_the_same_BatchRequestId_only_one_set_of_rows_persists()
    {
        var scene = await ArrangeSaleAsync(qty: 10m, deliveryRequiredQuantity: 4m);
        var batchId = Guid.NewGuid();
        var batchRequest = new CreateDeliveryReceiptBatchRequest(batchId, new[] { FullyClaimingRequest(scene, 4m) });

        HttpRequestMessage BatchRequest() => AuthorizedRequest(
            HttpMethod.Post, $"/api/sales/{scene.SaleId}/delivery-receipts/batch", scene.Token, batchRequest);

        var results = await Task.WhenAll(Client.SendAsync(BatchRequest()), Client.SendAsync(BatchRequest()));

        // Both responses must succeed (idempotent — a retry is never an error) and describe the SAME
        // underlying batch, whether one raced ahead as the "real" creator or the other found it already
        // committed via the post-lock re-check.
        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        var bodies = await Task.WhenAll(results.Select(r => r.Content.ReadFromJsonAsync<DeliveryReceiptBatchResultDto>(TestJson.Options)));
        bodies[0]!.Created.Single().Id.Should().Be(bodies[1]!.Created.Single().Id);

        await InScopeAsync(async db =>
        {
            (await db.DeliveryReceipts.CountAsync(d => d.SaleId == scene.SaleId)).Should().Be(1); // never duplicated
            return true;
        });
    }
}
