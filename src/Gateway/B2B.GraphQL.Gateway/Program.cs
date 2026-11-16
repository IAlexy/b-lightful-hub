using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.GraphQL.Gateway.GraphQL;
using Catalog.Service;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.AspNetCore.Mvc;
using Ordering.Service;
using Ordering.Service.DataModels;
using Ordering.Service.GraphQL;

var builder = WebApplication.CreateBuilder(args);

// 1. Register Core BuildingBlocks (CQRS Dispatcher, Compiled/Profile Object Mapper, Event Bus)
builder.Services.AddSingleton<ISender, CqrsDispatcher>();
builder.Services.AddSingleton<IB2BMapper, B2BMapper>();
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();

// 2. Register the 4 B2B Microservice Bounded Contexts
builder.Services.AddCatalogMicroservice();
builder.Services.AddOrderingMicroservice();
builder.Services.AddInvoicingMicroservice();
builder.Services.AddDeliveryMicroservice();

// 3. Register Federated GraphQL Engine
builder.Services.AddSingleton<B2BFederatedGraphQLService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Seed an initial confirmed B2B Order so the Federated GraphQL & REST endpoints have live stitched data on startup
using (var scope = app.Services.CreateScope())
{
    var sender = scope.ServiceProvider.GetRequiredService<ISender>();
    var existing = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(new GetPurchaseOrdersQuery());
    if (existing.Count == 0)
    {
        await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Alpine Cloud Datacenters SAS",
                BuyerVatNumber: "FR42889123456",
                PaymentTermsDays: 30,
                ShippingDestination: "Parc Technologique de Grenoble, 38000 France",
                Items:
                [
                    new OrderLineRequest("SRV-RACK-42U", 6),
                    new OrderLineRequest("SW-FIBER-100G", 4)
                ],
                AutoConfirm: true));
    }
}

// ==========================================
// 1. FEDERATED GRAPHQL ENDPOINTS (/graphql)
// ==========================================

app.MapGet("/graphql/schema", () => Results.Text(B2BFederatedGraphQLService.SdlSchema, "text/plain"))
   .WithName("GetGraphQLSchema")
   .WithOpenApi();

app.MapPost("/graphql", async ([FromBody] GraphQLRequest request, B2BFederatedGraphQLService graphQL, CancellationToken ct) =>
{
    var response = await graphQL.ExecuteAsync(request, ct);
    return Results.Ok(response);
})
.WithName("ExecuteGraphQL")
.WithOpenApi();

// ==========================================
// 2. CATALOG MICROSERVICE REST ENDPOINTS
// ==========================================

app.MapGet("/api/catalog/products", async ([FromQuery] string? category, ISender sender, CancellationToken ct) =>
{
    var products = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
        new GetCatalogProductsQuery(category), ct);
    return Results.Ok(products);
})
.WithName("GetCatalogProducts")
.WithOpenApi();

app.MapPost("/api/catalog/products", async ([FromBody] CreateCatalogProductCommand command, ISender sender, CancellationToken ct) =>
{
    var created = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(command, ct);
    return Results.Created($"/api/catalog/products/{created.Sku}", created);
})
.WithName("CreateCatalogProduct")
.WithOpenApi();

// ==========================================
// 3. ORDERING MICROSERVICE REST ENDPOINTS
// ==========================================

app.MapGet("/api/ordering/orders", async ([FromQuery] string? buyerVatNumber, ISender sender, CancellationToken ct) =>
{
    var orders = await sender.SendQueryAsync<GetPurchaseOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
        new GetPurchaseOrdersQuery(buyerVatNumber), ct);
    return Results.Ok(orders);
})
.WithName("GetPurchaseOrders")
.WithOpenApi();

app.MapGet("/api/ordering/orders/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var order = await sender.SendQueryAsync<GetPurchaseOrderByIdQuery, PurchaseOrderDto?>(
        new GetPurchaseOrderByIdQuery(id), ct);
    return order is null ? Results.NotFound() : Results.Ok(order);
})
.WithName("GetPurchaseOrderById")
.WithOpenApi();

app.MapPost("/api/ordering/orders", async ([FromBody] CreatePurchaseOrderCommand command, ISender sender, CancellationToken ct) =>
{
    var created = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(command, ct);
    return Results.Created($"/api/ordering/orders/{created.Id}", created);
})
.WithName("CreatePurchaseOrder")
.WithOpenApi();

app.MapDelete("/api/ordering/orders/{orderId:guid}", async (Guid orderId, [FromQuery] string? reason, ISender sender, CancellationToken ct) =>
{
    try
    {
        var result = await sender.SendCommandAsync<CancelPurchaseOrderCommand, OrderCancellationResultDto>(
            new CancelPurchaseOrderCommand(orderId, reason), ct);
        return Results.Ok(result);
    }
    catch (OrderCancellationException ex) when (ex.ErrorCode == "ORDER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (OrderCancellationException ex)
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CancelPurchaseOrder")
.WithOpenApi();

app.MapDelete("/api/ordering/orders/{orderId:guid}/lines/{lineId:guid}", async (Guid orderId, Guid lineId, [FromQuery] string? reason, ISender sender, CancellationToken ct) =>
{
    try
    {
        var result = await sender.SendCommandAsync<CancelOrderLineCommand, OrderCancellationResultDto>(
            new CancelOrderLineCommand(orderId, lineId, reason), ct);
        return Results.Ok(result);
    }
    catch (OrderCancellationException ex) when (ex.ErrorCode is "ORDER_NOT_FOUND" or "ORDER_LINE_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (OrderCancellationException ex)
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CancelOrderLine")
.WithOpenApi();

app.MapPost("/api/ordering/graphql", async ([FromBody] OrderingGraphQLRequest request, OrderingGraphQLResolver resolver, CancellationToken ct) =>
{
    var response = await resolver.ExecuteAsync(request, ct);
    return Results.Ok(response);
})
.WithName("ExecuteOrderingSubgraphGraphQL")
.WithOpenApi();

// ==========================================
// 4. INVOICING MICROSERVICE REST ENDPOINTS
// ==========================================

app.MapGet("/api/invoicing/invoices", async (ISender sender, CancellationToken ct) =>
{
    var invoices = await sender.SendQueryAsync<GetInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(new GetInvoicesQuery(), ct);
    return Results.Ok(invoices);
})
.WithName("GetInvoices")
.WithOpenApi();

app.MapPatch("/api/invoicing/invoices/{id:guid}/pay", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var updated = await sender.SendCommandAsync<MarkInvoicePaidCommand, B2BInvoiceDto?>(new MarkInvoicePaidCommand(id), ct);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
})
.WithName("MarkInvoicePaid")
.WithOpenApi();

// ==========================================
// 5. DELIVERY MICROSERVICE REST ENDPOINTS
// ==========================================

app.MapGet("/api/delivery/shipments", async (ISender sender, CancellationToken ct) =>
{
    var shipments = await sender.SendQueryAsync<GetShipmentsQuery, IReadOnlyList<B2BShipmentDto>>(new GetShipmentsQuery(), ct);
    return Results.Ok(shipments);
})
.WithName("GetShipments")
.WithOpenApi();

app.MapPatch("/api/delivery/shipments/{id:guid}/advance", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var updated = await sender.SendCommandAsync<AdvanceShipmentStatusCommand, B2BShipmentDto?>(new AdvanceShipmentStatusCommand(id), ct);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
})
.WithName("AdvanceShipmentStatus")
.WithOpenApi();

await app.RunAsync();

public partial class Program
{
    protected Program() { }
}
