namespace B2B.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoMapper;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using Catalog.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;
using Ordering.Service.DataModels;
using Ordering.Service.GraphQL;
using Ordering.Service.Mapping;

public class OrderCancellationUnitTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OrderCancellationUnitTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static ServiceProvider BuildIsolatedServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ISender, CqrsDispatcher>();
        services.AddSingleton<IB2BMapper, B2BMapper>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddCatalogMicroservice();
        services.AddOrderingMicroservice();
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AutoMapper_Should_Map_CancelCommands_To_DataModels_And_CancellationResultDto()
    {
        var config = new MapperConfiguration(cfg => cfg.AddProfile<OrderingAutoMapperProfile>());
        var mapper = config.CreateMapper();

        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        var cancelOrderDm = mapper.Map<CancelPurchaseOrderCommand, CancelOrderDataModel>(
            new CancelPurchaseOrderCommand(orderId, "  Project postponed  "));
        Assert.Equal(orderId, cancelOrderDm.OrderId);
        Assert.Equal("Project postponed", cancelOrderDm.Reason);

        var cancelLineDm = mapper.Map<CancelOrderLineCommand, CancelOrderLineDataModel>(
            new CancelOrderLineCommand(orderId, lineId, null));
        Assert.Equal(orderId, cancelLineDm.OrderId);
        Assert.Equal(lineId, cancelLineDm.OrderLineId);
        Assert.Equal("Order line removed by B2B customer", cancelLineDm.Reason);
    }

    [Fact]
    public async Task CancelOrder_Should_Cancel_Unpaid_Order_And_Reject_Second_Cancellation()
    {
        await using var provider = BuildIsolatedServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var order = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Annecy Alpine Tech SAS",
                BuyerVatNumber: "FR7411223344",
                PaymentTermsDays: 30,
                ShippingDestination: "74000 Annecy, France",
                Items: [new OrderLineRequest("SRV-RACK-42U", 2)]));

        var cancelResult = await sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
            new CancelPurchaseOrderCommand(order.Id, "Budget reallocation"));

        Assert.Equal(order.Id, cancelResult.OrderId);
        Assert.Equal("Cancelled", cancelResult.Status);
        Assert.Equal(0m, cancelResult.RemainingTotalAmountHt);
        Assert.Equal(0, cancelResult.RemainingLineCount);
        Assert.Equal("Budget reallocation", cancelResult.CancellationReason);
        Assert.NotNull(cancelResult.CancelledAtUtc);

        // Trying to cancel an already cancelled order must throw ORDER_ALREADY_CANCELLED
        var ex = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
                new CancelPurchaseOrderCommand(order.Id, "Duplicate cancel")));
        Assert.Equal("ORDER_ALREADY_CANCELLED", ex.ErrorCode);
    }

    [Fact]
    public async Task CancelOrderLine_Should_Recalculate_Total_And_AutoCancel_Order_When_Last_Line_Removed()
    {
        await using var provider = BuildIsolatedServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var order = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Montpellier MedTech SA",
                BuyerVatNumber: "FR3499887766",
                PaymentTermsDays: 30,
                ShippingDestination: "34000 Montpellier, France",
                Items:
                [
                    new OrderLineRequest("SRV-RACK-42U", 2),   // 2 * 2400 = 4800 EUR HT
                    new OrderLineRequest("UPS-LFP-10KVA", 1)   // 1 * 1950 = 1950 EUR HT
                ]));

        Assert.Equal(2, order.Lines.Count);
        Assert.Equal(6750m, order.TotalAmountHt);

        var firstLineId = order.Lines[0].Id;
        var secondLineId = order.Lines[1].Id;

        // 1. Cancel first order line -> Order stays Confirmed with 1 remaining line (1950 EUR HT)
        var afterFirstRemoval = await sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
            new CancelOrderLineCommand(order.Id, firstLineId, "Remove server racks"));

        Assert.Equal("Confirmed", afterFirstRemoval.Status);
        Assert.Equal(firstLineId, afterFirstRemoval.CancelledOrderLineId);
        Assert.Equal(1, afterFirstRemoval.RemainingLineCount);
        Assert.Equal(1950m, afterFirstRemoval.RemainingTotalAmountHt);

        // 2. Cancel the last remaining order line -> Order automatically transitions to Cancelled
        var afterLastRemoval = await sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
            new CancelOrderLineCommand(order.Id, secondLineId, "Remove last line"));

        Assert.Equal("Cancelled", afterLastRemoval.Status);
        Assert.Equal(secondLineId, afterLastRemoval.CancelledOrderLineId);
        Assert.Equal(0, afterLastRemoval.RemainingLineCount);
        Assert.Equal(0m, afterLastRemoval.RemainingTotalAmountHt);
        Assert.NotNull(afterLastRemoval.CancelledAtUtc);
    }

    [Fact]
    public async Task CancelOrder_And_CancelOrderLine_Should_Throw_NotFound_When_Order_Or_Line_Does_Not_Exist()
    {
        await using var provider = BuildIsolatedServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var nonExistentOrderId = Guid.NewGuid();
        var nonExistentLineId = Guid.NewGuid();

        // 1. Cancel non-existent order -> ORDER_NOT_FOUND
        var orderEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
                new CancelPurchaseOrderCommand(nonExistentOrderId)));
        Assert.Equal("ORDER_NOT_FOUND", orderEx.ErrorCode);

        // 2. Cancel line on non-existent order -> ORDER_NOT_FOUND
        var lineOrderEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
                new CancelOrderLineCommand(nonExistentOrderId, nonExistentLineId)));
        Assert.Equal("ORDER_NOT_FOUND", lineOrderEx.ErrorCode);

        // 3. Create valid order, then try to delete a non-existent OrderLineId -> ORDER_LINE_NOT_FOUND
        var order = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Rennes Cyber Defense",
                BuyerVatNumber: "FR3512345678",
                PaymentTermsDays: 30,
                ShippingDestination: "35000 Rennes",
                Items: [new OrderLineRequest("SW-FIBER-100G", 1)]));

        var missingLineEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
                new CancelOrderLineCommand(order.Id, nonExistentLineId)));
        Assert.Equal("ORDER_LINE_NOT_FOUND", missingLineEx.ErrorCode);
    }

    [Fact]
    public async Task CancelOrder_And_CancelOrderLine_Should_Reject_PartiallyPaid_And_FullyPaid_Orders()
    {
        await using var provider = BuildIsolatedServiceProvider();
        var sender = provider.GetRequiredService<ISender>();

        var order = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Nice Côte d'Azur Datacenter",
                BuyerVatNumber: "FR0699887766",
                PaymentTermsDays: 30,
                ShippingDestination: "Sophia Antipolis, 06560 Valbonne",
                Items:
                [
                    new OrderLineRequest("SRV-RACK-42U", 2), // 4800 EUR HT
                    new OrderLineRequest("SW-FIBER-100G", 1) // 4800 EUR HT -> Total = 9600 EUR HT
                ]));

        var lineId = order.Lines[0].Id;

        // 1. Record a PARTIAL payment (3000 EUR out of 9600 EUR HT)
        var partiallyPaidOrder = await sender.SendCommandAsync<RecordOrderPaymentCommand, PurchaseOrderDto>(
            new RecordOrderPaymentCommand(order.Id, 3000m));
        Assert.Equal("PartiallyPaid", partiallyPaidOrder.PaymentStatus);
        Assert.Equal(3000m, partiallyPaidOrder.PaidAmountHt);

        // Trying to cancel a partially paid order must fail with ORDER_PARTIALLY_PAID
        var partialOrderEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
                new CancelPurchaseOrderCommand(order.Id, "Try cancel partially paid")));
        Assert.Equal("ORDER_PARTIALLY_PAID", partialOrderEx.ErrorCode);

        // Trying to cancel a line on a partially paid order must also fail with ORDER_PARTIALLY_PAID
        var partialLineEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
                new CancelOrderLineCommand(order.Id, lineId, "Try remove line on partially paid")));
        Assert.Equal("ORDER_PARTIALLY_PAID", partialLineEx.ErrorCode);

        // 2. Record the remaining payment (6600 EUR HT) -> Order becomes FULLY PAID
        var fullyPaidOrder = await sender.SendCommandAsync<RecordOrderPaymentCommand, PurchaseOrderDto>(
            new RecordOrderPaymentCommand(order.Id, 6600m));
        Assert.Equal("Paid", fullyPaidOrder.PaymentStatus);
        Assert.Equal(9600m, fullyPaidOrder.PaidAmountHt);

        // Trying to cancel a fully paid order must fail with ORDER_ALREADY_PAID
        var paidOrderEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
                new CancelPurchaseOrderCommand(order.Id, "Try cancel fully paid")));
        Assert.Equal("ORDER_ALREADY_PAID", paidOrderEx.ErrorCode);

        // Trying to cancel a line on a fully paid order must fail with ORDER_ALREADY_PAID
        var paidLineEx = await Assert.ThrowsAsync<OrderCancellationException>(() =>
            sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
                new CancelOrderLineCommand(order.Id, lineId, "Try remove line on fully paid")));
        Assert.Equal("ORDER_ALREADY_PAID", paidLineEx.ErrorCode);
    }

    [Fact]
    public async Task GraphQL_And_Http_Delete_Should_Cancel_Orders_And_Lines_And_Handle_Error_Cases()
    {
        await using var provider = BuildIsolatedServiceProvider();
        var resolver = provider.GetRequiredService<OrderingGraphQLResolver>();

        // Create an order with 2 lines via GraphQL
        var createRes = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "mutation { createOrder { id lines { id sku } } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["buyerCompanyName"] = JsonSerializer.SerializeToElement("Zürich FinTech AG"),
                ["buyerVatNumber"] = JsonSerializer.SerializeToElement("CHE999888777"),
                ["paymentTermsDays"] = JsonSerializer.SerializeToElement(30),
                ["shippingDestination"] = JsonSerializer.SerializeToElement("Bahnhofstrasse, Zürich"),
                ["items"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { sku = "SRV-RACK-42U", quantity = 2 },
                    new { sku = "SW-FIBER-100G", quantity = 1 }
                })
            }));

        var created = Assert.IsType<PurchaseOrderDto>(createRes.Data!["createOrder"]);
        var firstLineId = created.Lines[0].Id;

        // 1. GraphQL cancelOrderLine mutation
        var cancelLineRes = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "mutation($orderId: ID!, $lineId: ID!, $reason: String) { cancelOrderLine(orderId: $orderId, lineId: $lineId, reason: $reason) { orderId status remainingLineCount cancelledOrderLineId } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["orderId"] = JsonSerializer.SerializeToElement(created.Id.ToString()),
                ["lineId"] = JsonSerializer.SerializeToElement(firstLineId.ToString()),
                ["reason"] = JsonSerializer.SerializeToElement("Adjusted rack count")
            }));

        Assert.Null(cancelLineRes.Errors);
        var lineResult = Assert.IsType<OrderCancellationResultDto>(cancelLineRes.Data!["cancelOrderLine"]);
        Assert.Equal("Confirmed", lineResult.Status);
        Assert.Equal(1, lineResult.RemainingLineCount);

        // 2. GraphQL cancelOrder mutation
        var cancelOrderRes = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "mutation($orderId: ID!, $reason: String) { cancelOrder(orderId: $orderId, reason: $reason) { orderId status remainingTotalAmountHt } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["orderId"] = JsonSerializer.SerializeToElement(created.Id.ToString()),
                ["reason"] = JsonSerializer.SerializeToElement("Order cancelled via GraphQL")
            }));

        Assert.Null(cancelOrderRes.Errors);
        var orderResult = Assert.IsType<OrderCancellationResultDto>(cancelOrderRes.Data!["cancelOrder"]);
        Assert.Equal("Cancelled", orderResult.Status);
        Assert.Equal(0m, orderResult.RemainingTotalAmountHt);

        // 3. HTTP DELETE endpoint tests (404 NotFound on unknown ID, 200 OK on valid, 409 Conflict on already cancelled)
        using var client = _factory.CreateClient();
        var notFoundDel = await client.DeleteAsync($"/api/ordering/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, notFoundDel.StatusCode);

        var httpOrderRes = await client.PostAsJsonAsync("/api/ordering/orders", new CreatePurchaseOrderCommand(
            BuyerCompanyName: "Bruxelles Cloud NV",
            BuyerVatNumber: "BE0123456789",
            PaymentTermsDays: 30,
            ShippingDestination: "Brussels",
            Items: [new OrderLineRequest("SRV-RACK-42U", 1)]));
        var httpOrder = await httpOrderRes.Content.ReadFromJsonAsync<PurchaseOrderDto>();
        Assert.NotNull(httpOrder);

        var okDel = await client.DeleteAsync($"/api/ordering/orders/{httpOrder.Id}?reason=ClientRequest");
        Assert.Equal(HttpStatusCode.OK, okDel.StatusCode);

        var conflictDel = await client.DeleteAsync($"/api/ordering/orders/{httpOrder.Id}");
        Assert.Equal(HttpStatusCode.Conflict, conflictDel.StatusCode);
    }
}
