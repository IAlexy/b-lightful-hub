using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.BuildingBlocks.Notifications;
using B2B.GraphQL.Gateway.GraphQL;
using Catalog.Service;
using Catalog.Service.DataModels;
using Catalog.Service.GraphQL;
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

// 1. Register Core BuildingBlocks (CQRS Dispatcher, Compiled/Profile Object Mapper, Event Bus, Email Notifications)
builder.Services.AddSingleton<ISender, CqrsDispatcher>();
builder.Services.AddSingleton<IB2BMapper, B2BMapper>();
builder.Services.AddSingleton<IEventBus, InMemoryEventBus>();
builder.Services.AddSingleton<IEmailNotificationService, InMemoryEmailNotificationService>();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Seed an initial B2B Customer & confirmed Order so the Federated GraphQL & REST endpoints have live stitched data on startup
using (var scope = app.Services.CreateScope())
{
    var sender = scope.ServiceProvider.GetRequiredService<ISender>();
    var existingCustomers = await sender.SendQueryAsync<GetB2BCustomersQuery, IReadOnlyList<B2BCustomerDto>>(new GetB2BCustomersQuery());
    if (existingCustomers.Count == 0)
    {
        await sender.SendCommandAsync<CreateB2BCustomerCommand, B2BCustomerDto>(
            new CreateB2BCustomerCommand(
                Name: "Alpine Cloud Datacenters SAS",
                SirenSiret: "42889123456001",
                InvoicingAddress: "Parc Technologique de Grenoble, 38000 France",
                DeliveryAddress: null,
                UseInvoicingAddressForDelivery: true,
                ContactEmail: "procurement@alpine-cloud.fr",
                PhoneNumber: "+33 4 76 00 11 22",
                Tags:
                [
                    new CustomTagInput("VIP", "Strategic Account", "#7C3AED"),
                    new CustomTagInput("Datacenter", "Tier-IV", "#10B981")
                ]));
    }

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
// 2. CATALOG MICROSERVICE REST & GRAPHQL ENDPOINTS
// ==========================================

app.MapGet("/api/catalog/products", async (
    [FromQuery] string? category,
    [FromQuery] string? search,
    [FromQuery] string? tag,
    [FromQuery] bool? lowStockOnly,
    [FromQuery] bool? inStockOnly,
    [FromQuery] bool? includeArchived,
    ISender sender,
    CancellationToken ct) =>
{
    var products = await sender.SendQueryAsync<GetCatalogProductsQuery, IReadOnlyList<CatalogProductDto>>(
        new GetCatalogProductsQuery(category, search, tag, lowStockOnly, inStockOnly, includeArchived ?? false), ct);
    return Results.Ok(products);
})
.WithName("GetCatalogProducts")
.WithOpenApi();

app.MapGet("/api/catalog/products/{id:guid}", async (Guid id, [FromQuery] bool? includeArchived, ISender sender, CancellationToken ct) =>
{
    var product = await sender.SendQueryAsync<GetCatalogProductByIdQuery, CatalogProductDto?>(
        new GetCatalogProductByIdQuery(id, includeArchived ?? true), ct);
    return product is null
        ? Results.NotFound(new { errorCode = "PRODUCT_NOT_FOUND", message = $"Catalog product {id} was not found." })
        : Results.Ok(product);
})
.WithName("GetCatalogProductById")
.WithOpenApi();

app.MapGet("/api/catalog/products/sku/{sku}", async (string sku, [FromQuery] bool? includeArchived, ISender sender, CancellationToken ct) =>
{
    var product = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
        new GetCatalogProductBySkuQuery(sku, includeArchived ?? false), ct);
    return product is null
        ? Results.NotFound(new { errorCode = "PRODUCT_NOT_FOUND", message = $"Catalog product '{sku}' was not found." })
        : Results.Ok(product);
})
.WithName("GetCatalogProductBySku")
.WithOpenApi();

app.MapGet("/api/catalog/products/sku/{sku}/availability", async (string sku, [FromQuery] int? quantity, ISender sender, CancellationToken ct) =>
{
    var check = await sender.SendQueryAsync<ValidateProductAvailabilityQuery, ProductAvailabilityCheckDto>(
        new ValidateProductAvailabilityQuery(sku, quantity ?? 1), ct);
    return Results.Ok(check);
})
.WithName("ValidateCatalogProductAvailability")
.WithOpenApi();

app.MapPost("/api/catalog/products", async ([FromBody] CreateCatalogProductCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<CreateCatalogProductCommand, CatalogProductDto>(command, ct);
        return Results.Created($"/api/catalog/products/{created.Id}", created);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "DUPLICATE_SKU")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode is "CATEGORY_NOT_FOUND" or "PRODUCT_TAG_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CreateCatalogProduct")
.WithOpenApi();

app.MapPut("/api/catalog/products/{id:guid}", async (Guid id, [FromBody] UpdateCatalogProductCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<UpdateCatalogProductCommand, CatalogProductDto>(
            command with { ProductId = id }, ct);
        return Results.Ok(updated);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode is "PRODUCT_NOT_FOUND" or "CATEGORY_NOT_FOUND" or "PRODUCT_TAG_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("UpdateCatalogProduct")
.WithOpenApi();

app.MapDelete("/api/catalog/products/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var archived = await sender.SendCommandAsync<ArchiveCatalogProductCommand, CatalogProductDto>(
            new ArchiveCatalogProductCommand(id), ct);
        return Results.Ok(archived);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("ArchiveCatalogProduct")
.WithOpenApi();

app.MapPost("/api/catalog/products/{id:guid}/unarchive", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var restored = await sender.SendCommandAsync<UnarchiveCatalogProductCommand, CatalogProductDto>(
            new UnarchiveCatalogProductCommand(id), ct);
        return Results.Ok(restored);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("UnarchiveCatalogProduct")
.WithOpenApi();

app.MapPut("/api/catalog/products/{id:guid}/volume-tiers", async (
    Guid id,
    [FromBody] IReadOnlyList<VolumePriceTierInput> tiers,
    ISender sender,
    CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<SetProductVolumePriceTiersCommand, CatalogProductDto>(
            new SetProductVolumePriceTiersCommand(id, tiers), ct);
        return Results.Ok(updated);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("SetProductVolumePriceTiers")
.WithOpenApi();

app.MapPost("/api/catalog/products/{id:guid}/restock", async (
    Guid id,
    [FromBody] RestockCatalogProductCommand command,
    ISender sender,
    CancellationToken ct) =>
{
    try
    {
        var result = await sender.SendCommandAsync<RestockCatalogProductCommand, RestockResultDto>(
            command with { ProductId = id }, ct);
        return Results.Ok(result);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("RestockCatalogProduct")
.WithOpenApi();

app.MapPatch("/api/catalog/products/{id:guid}/stock", async (
    Guid id,
    [FromBody] AdjustCatalogStockCommand command,
    ISender sender,
    CancellationToken ct) =>
{
    try
    {
        var result = await sender.SendCommandAsync<AdjustCatalogStockCommand, RestockResultDto>(
            command with { ProductId = id }, ct);
        return Results.Ok(result);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("AdjustCatalogStock")
.WithOpenApi();

// Back-in-stock refill email alerts
app.MapGet("/api/catalog/products/{id:guid}/refill-alerts", async (
    Guid id,
    [FromQuery] bool? pendingOnly,
    ISender sender,
    CancellationToken ct) =>
{
    try
    {
        var alerts = await sender.SendQueryAsync<GetProductRefillAlertsQuery, IReadOnlyList<StockRefillAlertDto>>(
            new GetProductRefillAlertsQuery(id, pendingOnly ?? true), ct);
        return Results.Ok(alerts);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("GetProductRefillAlerts")
.WithOpenApi();

app.MapPost("/api/catalog/products/{id:guid}/refill-alerts", async (
    Guid id,
    [FromBody] SubscribeStockRefillAlertCommand command,
    ISender sender,
    CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<SubscribeStockRefillAlertCommand, StockRefillAlertDto>(
            command with { ProductId = id }, ct);
        return Results.Created($"/api/catalog/refill-alerts/{created.Id}", created);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode is "PRODUCT_IN_STOCK" or "DUPLICATE_ALERT_SUBSCRIPTION" or "PRODUCT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("SubscribeStockRefillAlert")
.WithOpenApi();

app.MapDelete("/api/catalog/refill-alerts/{alertId:guid}", async (Guid alertId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var deleted = await sender.SendCommandAsync<UnsubscribeStockRefillAlertCommand, bool>(
            new UnsubscribeStockRefillAlertCommand(alertId), ct);
        return Results.Ok(new { alertId, deleted });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "ALERT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("UnsubscribeStockRefillAlert")
.WithOpenApi();

// Relational Catalog Categories CRUD
app.MapGet("/api/catalog/categories", async ([FromQuery] string? search, ISender sender, CancellationToken ct) =>
{
    var categories = await sender.SendQueryAsync<GetCatalogCategoriesQuery, IReadOnlyList<CatalogCategoryDto>>(
        new GetCatalogCategoriesQuery(search), ct);
    return Results.Ok(categories);
})
.WithName("GetCatalogCategories")
.WithOpenApi();

app.MapGet("/api/catalog/categories/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    var category = await sender.SendQueryAsync<GetCatalogCategoryByIdQuery, CatalogCategoryDto?>(
        new GetCatalogCategoryByIdQuery(id), ct);
    return category is null
        ? Results.NotFound(new { errorCode = "CATEGORY_NOT_FOUND", message = $"Catalog category {id} was not found." })
        : Results.Ok(category);
})
.WithName("GetCatalogCategoryById")
.WithOpenApi();

app.MapPost("/api/catalog/categories", async ([FromBody] CreateCatalogCategoryCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<CreateCatalogCategoryCommand, CatalogCategoryDto>(command, ct);
        return Results.Created($"/api/catalog/categories/{created.Id}", created);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "DUPLICATE_CATEGORY_NAME")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CreateCatalogCategory")
.WithOpenApi();

app.MapPut("/api/catalog/categories/{id:guid}", async (Guid id, [FromBody] UpdateCatalogCategoryCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<UpdateCatalogCategoryCommand, CatalogCategoryDto>(
            command with { CategoryId = id }, ct);
        return Results.Ok(updated);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "CATEGORY_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "DUPLICATE_CATEGORY_NAME")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("UpdateCatalogCategory")
.WithOpenApi();

app.MapDelete("/api/catalog/categories/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var deleted = await sender.SendCommandAsync<DeleteCatalogCategoryCommand, bool>(
            new DeleteCatalogCategoryCommand(id), ct);
        return Results.Ok(new { categoryId = id, deleted });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "CATEGORY_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "CATEGORY_IN_USE")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("DeleteCatalogCategory")
.WithOpenApi();

// Independent Product Tags CRUD & Product <-> Tag Associations (3-table pattern)
app.MapGet("/api/catalog/tags", async ([FromQuery] string? search, ISender sender, CancellationToken ct) =>
{
    var tags = await sender.SendQueryAsync<GetProductTagsQuery, IReadOnlyList<ProductTagDto>>(
        new GetProductTagsQuery(search), ct);
    return Results.Ok(tags);
})
.WithName("GetProductTags")
.WithOpenApi();

app.MapGet("/api/catalog/tags/{tagId:guid}", async (Guid tagId, ISender sender, CancellationToken ct) =>
{
    var tag = await sender.SendQueryAsync<GetProductTagByIdQuery, ProductTagDto?>(
        new GetProductTagByIdQuery(tagId), ct);
    return tag is null
        ? Results.NotFound(new { errorCode = "PRODUCT_TAG_NOT_FOUND", message = $"Product tag {tagId} was not found." })
        : Results.Ok(tag);
})
.WithName("GetProductTagById")
.WithOpenApi();

app.MapPost("/api/catalog/tags", async ([FromBody] CreateProductTagCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<CreateProductTagCommand, ProductTagDto>(command, ct);
        return Results.Created($"/api/catalog/tags/{created.Id}", created);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "DUPLICATE_PRODUCT_TAG_NAME")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("CreateProductTag")
.WithOpenApi();

app.MapDelete("/api/catalog/tags/{tagId:guid}", async (Guid tagId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var deleted = await sender.SendCommandAsync<DeleteProductTagCommand, bool>(
            new DeleteProductTagCommand(tagId), ct);
        return Results.Ok(new { tagId, deleted });
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode == "PRODUCT_TAG_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("DeleteProductTag")
.WithOpenApi();

app.MapPost("/api/catalog/products/{productId:guid}/tags/{tagId:guid}", async (Guid productId, Guid tagId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<AssignTagToProductCommand, CatalogProductDto>(
            new AssignTagToProductCommand(productId, tagId), ct);
        return Results.Ok(updated);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode is "PRODUCT_NOT_FOUND" or "PRODUCT_TAG_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("AssignTagToProduct")
.WithOpenApi();

app.MapDelete("/api/catalog/products/{productId:guid}/tags/{tagId:guid}", async (Guid productId, Guid tagId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<RemoveTagFromProductCommand, CatalogProductDto>(
            new RemoveTagFromProductCommand(productId, tagId), ct);
        return Results.Ok(updated);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode is "PRODUCT_NOT_FOUND" or "PRODUCT_TAG_ASSIGNMENT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("RemoveTagFromProduct")
.WithOpenApi();

app.MapPost("/api/catalog/graphql", async ([FromBody] CatalogGraphQLRequest request, CatalogGraphQLResolver resolver, CancellationToken ct) =>
{
    var response = await resolver.ExecuteAsync(request, ct);
    return Results.Ok(response);
})
.WithName("ExecuteCatalogSubgraphGraphQL")
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
})
.WithName("GetCustomerFavoriteProducts")
.WithOpenApi();

app.MapPost("/api/customers/{id:guid}/favorite-products/{productId:guid}", async (Guid id, Guid productId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var favorite = await sender.SendCommandAsync<AddManualFavoriteProductCommand, CustomerFavoriteProductDto>(
            new AddManualFavoriteProductCommand(id, productId), ct);
        return Results.Created($"/api/customers/{id}/favorite-products", favorite);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "DUPLICATE_FAVORITE" or "PRODUCT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("AddManualFavoriteProduct")
.WithOpenApi();

app.MapDelete("/api/customers/{id:guid}/favorite-products/{productId:guid}", async (Guid id, Guid productId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var removed = await sender.SendCommandAsync<RemoveManualFavoriteProductCommand, bool>(
            new RemoveManualFavoriteProductCommand(id, productId), ct);
        return Results.Ok(new { customerId = id, productId, removed });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "FAVORITE_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("RemoveManualFavoriteProduct")
.WithOpenApi();

app.MapGet("/api/customers/{id:guid}/wishlist", async (Guid id, ISender sender, CancellationToken ct) =>
{
    try
    {
        var wishlist = await sender.SendQueryAsync<GetCustomerWishlistQuery, IReadOnlyList<CustomerWishlistItemDto>>(
            new GetCustomerWishlistQuery(id), ct);
        return Results.Ok(wishlist);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode == "CUSTOMER_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("GetCustomerWishlist")
.WithOpenApi();

app.MapPost("/api/customers/{id:guid}/wishlist", async (Guid id, [FromBody] AddProductToWishlistCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var created = await sender.SendCommandAsync<AddProductToWishlistCommand, CustomerWishlistItemDto>(
            command with { CustomerId = id }, ct);
        return Results.Created($"/api/customers/{id}/wishlist", created);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "PRODUCT_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "DUPLICATE_WISHLIST_ITEM" or "PRODUCT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("AddProductToWishlist")
.WithOpenApi();

app.MapPut("/api/customers/{id:guid}/wishlist/{productId:guid}", async (Guid id, Guid productId, [FromBody] UpdateWishlistItemCommand command, ISender sender, CancellationToken ct) =>
{
    try
    {
        var updated = await sender.SendCommandAsync<UpdateWishlistItemCommand, CustomerWishlistItemDto>(
            command with { CustomerId = id, ProductId = productId }, ct);
        return Results.Ok(updated);
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "WISHLIST_ITEM_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CustomerDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("UpdateWishlistItem")
.WithOpenApi();

app.MapDelete("/api/customers/{id:guid}/wishlist/{productId:guid}", async (Guid id, Guid productId, ISender sender, CancellationToken ct) =>
{
    try
    {
        var removed = await sender.SendCommandAsync<RemoveProductFromWishlistCommand, bool>(
            new RemoveProductFromWishlistCommand(id, productId), ct);
        return Results.Ok(new { customerId = id, productId, removed });
    }
    catch (CustomerDomainException ex) when (ex.ErrorCode is "CUSTOMER_NOT_FOUND" or "WISHLIST_ITEM_NOT_FOUND")
    {
        return Results.NotFound(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
})
.WithName("RemoveProductFromWishlist")
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
    try
    {
        var created = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(command, ct);
        return Results.Created($"/api/ordering/orders/{created.Id}", created);
    }
    catch (CatalogDomainException ex) when (ex.ErrorCode is "OUT_OF_STOCK" or "INSUFFICIENT_STOCK" or "PRODUCT_ARCHIVED")
    {
        return Results.Conflict(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
    catch (CatalogDomainException ex)
    {
        return Results.BadRequest(new { errorCode = ex.ErrorCode, message = ex.Message });
    }
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
