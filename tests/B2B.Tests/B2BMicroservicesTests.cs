namespace B2B.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.GraphQL.Gateway.GraphQL;
using Catalog.Service;
using Delivery.Service;
using Invoicing.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;

public sealed record SourceSample(string Code, int Quantity, decimal UnitPrice);
public sealed record DestRecordSample(string Code, int Quantity, decimal UnitPrice);
public sealed class DestMutableSample
{
    public string Code { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class AuditPipelineBehavior<TRequest, TResponse>(List<string> log)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> HandleAsync(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct = default)
    {
        log.Add($"BEFORE:{typeof(TRequest).Name}");
        var response = await next();
        log.Add($"AFTER:{typeof(TRequest).Name}");
        return response;
    }
}

public class B2BMicroservicesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public B2BMicroservicesTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Mapper_Should_Map_Using_Explicit_Profile_And_Compiled_Expression_Fallback()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IB2BMapper, B2BMapper>();
        services.AddSingleton<IMapProfile<CatalogProduct, CatalogProductDto>, CatalogProductToDtoProfile>();
        using var provider = services.BuildServiceProvider();

        var mapper = provider.GetRequiredService<IB2BMapper>();

        // 1. Explicit Profile mapping (computes BulkDiscountPercent and IsLowStock)
        var product = new CatalogProduct
        {
            Sku = "TEST-SKU",
            Name = "Rack Server",
            Category = "Compute",
            UnitPriceHt = 1000m,
            BulkTierMinQuantity = 5,
            BulkUnitPriceHt = 800m,
            AvailableStock = 8,
            ReservedStock = 2,
            UnitWeightKg = 25.0
        };

        var dto = mapper.Map<CatalogProduct, CatalogProductDto>(product);
        Assert.Equal("TEST-SKU", dto.Sku);
        Assert.Equal(20.0m, dto.BulkDiscountPercent);
        Assert.True(dto.IsLowStock);

        // 2. Compiled Expression Tree fallback -> Positional Record
        var src = new SourceSample("B2B-99", 12, 49.5m);
        var destRecord = mapper.Map<SourceSample, DestRecordSample>(src);
        Assert.Equal("B2B-99", destRecord.Code);
        Assert.Equal(12, destRecord.Quantity);
        Assert.Equal(49.5m, destRecord.UnitPrice);

        // 3. Compiled Expression Tree fallback -> Mutable Class + MapList
        var destList = mapper.MapList<SourceSample, DestMutableSample>([src]);
        Assert.Single(destList);
        Assert.Equal("B2B-99", destList[0].Code);
        Assert.Equal(12, destList[0].Quantity);
    }

    [Fact]
    public async Task Cqrs_Pipeline_And_EventDriven_Saga_Should_Coordinate_All_4_Microservices()
    {
        var auditLog = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(auditLog);
        services.AddSingleton<ISender, CqrsDispatcher>();
        services.AddSingleton<IB2BMapper, B2BMapper>();
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(AuditPipelineBehavior<,>));

        services.AddCatalogMicroservice();
        services.AddOrderingMicroservice();
        services.AddInvoicingMicroservice();
        services.AddDeliveryMicroservice();

        await using var provider = services.BuildServiceProvider();
        var sender = provider.GetRequiredService<ISender>();
        var eventBus = provider.GetRequiredService<IEventBus>();

        // Confirm a B2B Purchase Order for 5 Server Racks (qualifies for bulk tier: 2040 EUR HT instead of 2400 EUR HT)
        var orderDto = await sender.SendCommandAsync<CreatePurchaseOrderCommand, PurchaseOrderDto>(
            new CreatePurchaseOrderCommand(
                BuyerCompanyName: "Hexagone Telecom SA",
                BuyerVatNumber: "FR1122334455",
                PaymentTermsDays: 45,
                ShippingDestination: "Marseille Hub Portuaire, France",
                Items: [new OrderLineRequest("SRV-RACK-42U", 5)],
                AutoConfirm: true));

        Assert.Equal("Confirmed", orderDto.Status);
        Assert.Equal(5 * 2040m, orderDto.TotalAmountHt); // 10,200 EUR HT bulk pricing applied!
        Assert.NotEmpty(auditLog);
        Assert.Single(eventBus.PublishedEvents);

        // Verify Catalog.Service reserved 5 units of SRV-RACK-42U (40 -> 35 available)
        var product = await sender.SendQueryAsync<GetCatalogProductBySkuQuery, CatalogProductDto?>(
            new GetCatalogProductBySkuQuery("SRV-RACK-42U"));
        Assert.NotNull(product);
        Assert.Equal(35, product.AvailableStock);
        Assert.Equal(7, product.ReservedStock);

        // Verify Invoicing.Service automatically issued the Net-45 Invoice with 20% VAT
        var invoice = await sender.SendQueryAsync<GetInvoiceByOrderIdQuery, B2BInvoiceDto?>(
            new GetInvoiceByOrderIdQuery(orderDto.Id));
        Assert.NotNull(invoice);
        Assert.Equal(10200m, invoice.SubtotalHt);
        Assert.Equal(2040m, invoice.VatAmount);
        Assert.Equal(12240m, invoice.TotalTtc);
        Assert.Equal("Issued", invoice.Status);

        // Verify Delivery.Service automatically scheduled heavy freight (5 * 85kg = 425kg -> 2 pallets via DHL Global Freight)
        var shipment = await sender.SendQueryAsync<GetShipmentByOrderIdQuery, B2BShipmentDto?>(
            new GetShipmentByOrderIdQuery(orderDto.Id));
        Assert.NotNull(shipment);
        Assert.Equal(425.0, shipment.TotalWeightKg);
        Assert.Equal(2, shipment.PalletCount);
        Assert.Equal("DHL Global Freight B2B", shipment.CarrierName);
        Assert.Equal("Preparing", shipment.Status);
    }

    [Fact]
    public async Task Federated_GraphQL_Gateway_Should_Execute_Stitched_Queries_And_Mutations()
    {
        using var client = _factory.CreateClient();

        // 1. Verify GraphQL SDL Schema endpoint
        var schemaRes = await client.GetAsync("/graphql/schema");
        Assert.Equal(HttpStatusCode.OK, schemaRes.StatusCode);
        var sdl = await schemaRes.Content.ReadAsStringAsync();
        Assert.Contains("type FederatedOrderNode", sdl);

        // 2. Execute Federated GraphQL Query combining Dashboard, Catalog, and Orders (with Invoice & Shipment)
        var queryPayload = new GraphQLRequest(
            Query: "query { __schema dashboard { totalProducts totalOrders } catalog { sku unitPriceHt } orders { id orderNumber invoice { invoiceNumber totalTtc } shipment { trackingNumber palletCount } } }");

        var gqlRes = await client.PostAsJsonAsync("/graphql", queryPayload);
        Assert.Equal(HttpStatusCode.OK, gqlRes.StatusCode);

        var gqlBody = await gqlRes.Content.ReadFromJsonAsync<GraphQLResponse>();
        Assert.NotNull(gqlBody);
        Assert.Null(gqlBody.Errors);
        Assert.NotNull(gqlBody.Data);
        Assert.True(gqlBody.Data.ContainsKey("dashboard"));
        Assert.True(gqlBody.Data.ContainsKey("catalog"));
        Assert.True(gqlBody.Data.ContainsKey("orders"));

        // 3. Execute GraphQL Mutation to create a new B2B Purchase Order and stitch its Invoice & Shipment
        var vars = new Dictionary<string, JsonElement>
        {
            ["buyerCompanyName"] = JsonSerializer.SerializeToElement("Nordic Edge Systems AB"),
            ["buyerVatNumber"] = JsonSerializer.SerializeToElement("SE5566778899"),
            ["paymentTermsDays"] = JsonSerializer.SerializeToElement(30),
            ["shippingDestination"] = JsonSerializer.SerializeToElement("Stockholm Kista Science City")
        };

        var mutationPayload = new GraphQLRequest(
            Query: "mutation CreateOrder { createPurchaseOrder { id orderNumber invoice { id status } shipment { id status } } }",
            Variables: vars);

        var mutRes = await client.PostAsJsonAsync("/graphql", mutationPayload);
        Assert.Equal(HttpStatusCode.OK, mutRes.StatusCode);
        var mutBody = await mutRes.Content.ReadFromJsonAsync<GraphQLResponse>();
        Assert.NotNull(mutBody?.Data);
        Assert.True(mutBody.Data.ContainsKey("createPurchaseOrder"));
    }

    [Fact]
    public async Task Microservice_Rest_Endpoints_Should_Create_And_Transition_B2B_Lifecycle()
    {
        using var client = _factory.CreateClient();

        // 1. Catalog REST
        var newProdRes = await client.PostAsJsonAsync("/api/catalog/products", new CreateCatalogProductCommand(
            Sku: "GPU-NODE-H200",
            Name: "Serveur IA 8x GPU Tensor Core",
            Category: "AI Compute",
            UnitPriceHt: 32000m,
            BulkTierMinQuantity: 2,
            BulkUnitPriceHt: 29500m,
            InitialStock: 10,
            UnitWeightKg: 65.0));
        Assert.Equal(HttpStatusCode.Created, newProdRes.StatusCode);

        var catalogRes = await client.GetFromJsonAsync<List<CatalogProductDto>>("/api/catalog/products?category=AI%20Compute");
        Assert.NotNull(catalogRes);
        Assert.Single(catalogRes);

        // 2. Ordering REST
        var orderRes = await client.PostAsJsonAsync("/api/ordering/orders", new CreatePurchaseOrderCommand(
            BuyerCompanyName: "DeepTech Labs",
            BuyerVatNumber: "FR7788990011",
            PaymentTermsDays: 30,
            ShippingDestination: "Paris Saclay, France",
            Items: [new OrderLineRequest("GPU-NODE-H200", 2)],
            AutoConfirm: true));
        Assert.Equal(HttpStatusCode.Created, orderRes.StatusCode);

        // 3. Invoicing REST: list and mark paid
        var invoices = await client.GetFromJsonAsync<List<B2BInvoiceDto>>("/api/invoicing/invoices");
        Assert.NotNull(invoices);
        Assert.NotEmpty(invoices);

        var payRes = await client.PatchAsync($"/api/invoicing/invoices/{invoices[0].Id}/pay", null);
        Assert.Equal(HttpStatusCode.OK, payRes.StatusCode);
        var paidInvoice = await payRes.Content.ReadFromJsonAsync<B2BInvoiceDto>();
        Assert.Equal("Paid", paidInvoice?.Status);

        // 4. Delivery REST: list and advance shipment status (Preparing -> InTransit -> Delivered)
        var shipments = await client.GetFromJsonAsync<List<B2BShipmentDto>>("/api/delivery/shipments");
        Assert.NotNull(shipments);
        Assert.NotEmpty(shipments);

        var shipId = shipments[0].Id;
        var transitRes = await client.PatchAsync($"/api/delivery/shipments/{shipId}/advance", null);
        var transitDto = await transitRes.Content.ReadFromJsonAsync<B2BShipmentDto>();
        Assert.Equal("InTransit", transitDto?.Status);

        var deliveredRes = await client.PatchAsync($"/api/delivery/shipments/{shipId}/advance", null);
        var deliveredDto = await deliveredRes.Content.ReadFromJsonAsync<B2BShipmentDto>();
        Assert.Equal("Delivered", deliveredDto?.Status);
    }
}
