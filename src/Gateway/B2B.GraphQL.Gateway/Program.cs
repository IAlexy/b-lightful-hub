using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.GraphQL.Gateway.DevEnvironment;
using B2B.GraphQL.Gateway.GraphQL;
using Catalog.Service;
using Customer.Service;
using Customer.Service.DataModels;
using Customer.Service.GraphQL;
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

// 2. Register the 5 B2B Microservice Bounded Contexts
builder.Services.AddCatalogMicroservice();
builder.Services.AddCustomerMicroservice();
builder.Services.AddOrderingMicroservice();
builder.Services.AddInvoicingMicroservice();
builder.Services.AddDeliveryMicroservice();

// 3. Register Federated GraphQL Engine
builder.Services.AddSingleton<B2BFederatedGraphQLService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Seed baseline data + extended Development mockup data so the Dev environment has live stitched data for API & UI tests
var seedSummary = await app.SeedDevMockDataAsync();
app.MapHealthEndpoint(seedSummary);

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.MapDevEnvironmentUi();
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
// 2.5 CUSTOMER MICROSERVICE REST & GRAPHQL ENDPOINTS
// ==========================================

app.MapGet("/api/customers", async ([FromQuery] string? search, [FromQuery] string? tag, ISender sender, CancellationToken ct) =>
{
    var customers = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(
        new GetB2BCustomersQuery(search, tag), ct);
    return Results.Ok(customers);
})
.WithName("GetB2BCustomers")
.WithOpenApi();

app.MapGet("/api/customers/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var customer = await sender.SendQueryAsync<GetB2BCustomerByIdQuery, B2BCustomerDto?>(
        new GetB2BCustomerByIdQuery(id), ct);
    return customer is null ? Results.NotFound(new { errorCode = "CUSTOMER_NOT_FOUND", message = $"Customer {id} was not found." }) : Results.Ok(customer);
})
.WithName("GetB2BCustomerById")
.WithOpenApi();

app.MapPost("/api/customers", async ([FromBody] CreateB2BCustomerCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(command, ct);
        return Results.Created($"/api/customers/{created.Id}", created);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "DUPLICATE_SIREN_SIRET")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CreateB2BCustomer")
.WithOpenApi();

app.MapPut("/api/customers/{id:guid}", async (Guid id, [FromBody] UpdateB2BCustomerCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var normalizedCommand = command with { CustomerId = id };
        var updated = await sender.SendCommandAsync<UpdateB2BCustomerCommand, B2BCustomerDto>(normalizedCommand, ct);
        return Results.Ok(updated);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "DUPLICATE_SIREN_SIRET")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("UpdateB2BCustomer")
.WithOpenApi();

app.MapDelete("/api/customers/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var deleted = await sender.SendCommandAsync<DeleteB2BCustomerCommand, bool>(new DeleteB2BCustomerCommand(id), ct);
        return Results.Ok(new { customerId = id, deleted });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("DeleteB2BCustomer")
.WithOpenApi();

// Independent Custom Tags CRUD & Customer <-> Tag Associations (3rd Table)
app.MapGet("/api/customers/tags", async ([FromQuery] string? search, ISender sender, CancellationToken ct) =>
{
    var tags = await sender.SendQueryAsync<GetCustomTagsQuery, IReadOnlyList<CustomTagDto>>(new GetCustomTagsQuery(search), ct);
    return Results.Ok(tags);
})
.WithName("GetCustomTags")
.WithOpenApi();

app.MapGet("/api/customers/tags/{tagId:guid}", async (Guid tagId, ISender sender, CancellationToken ct) =>
{
    var tag = await sender.SendQueryAsync<GetCustomTagByIdQuery, CustomTagDto?>(new GetCustomTagByIdQuery(tagId), ct);
    return tag is null ? Results.NotFound(new { errorCode = "TAG_NOT_FOUND", message = $"Custom tag {tagId} was not found." }) : Results.Ok(tag);
})
.WithName("GetCustomTagById")
.WithOpenApi();

app.MapPost("/api/customers/tags", async ([FromBody] CreateCustomTagCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<CreateCustomTagCommand, CustomTagDto>(command, ct);
        return Results.Created($"/api/customers/tags/{created.Id}", created);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "DUPLICATE_TAG_NAME")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CreateCustomTag")
.WithOpenApi();

app.MapDelete("/api/customers/tags/{tagId:guid}", async (Guid tagId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var deleted = await sender.SendCommandAsync<DeleteCustomTagCommand, bool>(new DeleteCustomTagCommand(tagId), ct);
        return Results.Ok(new { tagId, deleted });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "TAG_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("DeleteCustomTag")
.WithOpenApi();

app.MapPost("/api/customers/{customerId:guid}/tags/{tagId:guid}", async (Guid customerId, Guid tagId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<AssignTagToCustomerCommand, B2BCustomerDto>(
            new AssignTagToCustomerCommand(customerId, tagId), ct);
        return Results.Ok(updated);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "TAG_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("AssignTagToCustomer")
.WithOpenApi();

app.MapDelete("/api/customers/{customerId:guid}/tags/{tagId:guid}", async (Guid customerId, Guid tagId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<RemoveTagFromCustomerCommand, B2BCustomerDto>(
            new RemoveTagFromCustomerCommand(customerId, tagId), ct);
        return Results.Ok(updated);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "TAG_ASSIGNMENT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("RemoveTagFromCustomer")
.WithOpenApi();

app.MapGet("/api/customers/{id:guid}/orders", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var orders = await sender.SendQueryAsync<GetCustomerOrdersQuery, IReadOnlyList<PurchaseOrderDto>>(
            new GetCustomerOrdersQuery(id), ct);
        return Results.Ok(orders);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("GetCustomerOrders")
.WithOpenApi();

app.MapGet("/api/customers/{id:guid}/invoices", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var invoices = await sender.SendQueryAsync<GetCustomerInvoicesQuery, IReadOnlyList<B2BInvoiceDto>>(
            new GetCustomerInvoicesQuery(id), ct);
        return Results.Ok(invoices);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("GetCustomerInvoices")
.WithOpenApi();

app.MapGet("/api/customers/{id:guid}/deliveries", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var deliveries = await sender.SendQueryAsync<GetCustomerDeliveriesQuery, IReadOnlyList<B2BShipmentDto>>(
            new GetCustomerDeliveriesQuery(id), ct);
        return Results.Ok(deliveries);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("GetCustomerDeliveries")
.WithOpenApi();

app.MapGet("/api/customers/{id:guid}/favorite-products", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var favorites = await sender.SendQueryAsync<GetCustomerFavoriteProductsQuery, IReadOnlyList<CustomerFavoriteProductDto>>(
            new GetCustomerFavoriteProductsQuery(id), ct);
        return Results.Ok(favorites);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerFavoriteProductsNotImplementedException ex)
    {
        return Results.Json(
            new { errorCode = ex.ErrorCode, customerId = ex.CustomerId, message = ex.Message },
            statusCode: StatusCodes.Status501NotImplemented);
    }
})
.WithName("GetCustomerFavoriteProducts")
.WithOpenApi();

app.MapPost("/api/customers/graphql", async ([FromBody] CustomerGraphQLRequest request, CustomerGraphQLResolver resolver, CancellationToken ct) =>
{
    var response = await resolver.ExecuteAsync(request, ct);
    return Results.Ok(response);
})
.WithName("ExecuteCustomerSubgraphGraphQL")
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
