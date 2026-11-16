namespace B2B.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using B2B.BuildingBlocks.Cqrs;
using B2B.BuildingBlocks.Mapping;
using B2B.BuildingBlocks.Messaging;
using B2B.GraphQL.Gateway.GraphQL;
using Catalog.Service;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Service;
using Ordering.Service.GraphQL;

public class OrderingGraphQLTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OrderingGraphQLTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static ServiceProvider BuildOrderingGraphQLServiceProvider()
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
    public async Task OrderingGraphQLResolver_CreateOrder_Mutation_Should_Persist_Customer_Order_And_Lines()
    {
        await using var provider = BuildOrderingGraphQLServiceProvider();
        var resolver = provider.GetRequiredService<OrderingGraphQLResolver>();

        var variables = new Dictionary<string, JsonElement>
        {
            ["buyerCompanyName"] = JsonSerializer.SerializeToElement("Geneva Precision Robotics SA"),
            ["buyerVatNumber"] = JsonSerializer.SerializeToElement("CHE112233445"),
            ["contactEmail"] = JsonSerializer.SerializeToElement("supply@geneva-robotics.ch"),
            ["paymentTermsDays"] = JsonSerializer.SerializeToElement(45),
            ["shippingDestination"] = JsonSerializer.SerializeToElement("Rue du Mont-Blanc 18, 1201 Genève"),
            ["autoConfirm"] = JsonSerializer.SerializeToElement(true),
            ["items"] = JsonSerializer.SerializeToElement(new[]
            {
                new { sku = "SRV-RACK-42U", quantity = 5 },
                new { sku = "SW-FIBER-100G", quantity = 4 }
            })
        };

        var response = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "mutation CreateB2BOrder { createOrder { id orderNumber customer { id companyName vatNumber contactEmail } lines { sku quantity unitPriceHt lineTotalHt } totalAmountHt } }",
            Variables: variables));

        Assert.Null(response.Errors);
        Assert.NotNull(response.Data);
        Assert.True(response.Data.TryGetValue("createOrder", out var orderObj));

        var createdOrder = Assert.IsType<PurchaseOrderDto>(orderObj);
        Assert.Equal("Geneva Precision Robotics SA", createdOrder.Customer.CompanyName);
        Assert.Equal("CHE112233445", createdOrder.Customer.VatNumber);
        Assert.Equal("supply@geneva-robotics.ch", createdOrder.Customer.ContactEmail);
        Assert.Equal(2, createdOrder.Lines.Count);
        Assert.Equal(9, createdOrder.TotalUnits);
        // Bulk prices: (5 * 2040) + (4 * 4200) = 10200 + 16800 = 27000
        Assert.Equal(27000m, createdOrder.TotalAmountHt);
    }

    [Fact]
    public async Task OrderingGraphQLResolver_Orders_And_OrderById_Queries_Should_Return_List_And_Specific_Order()
    {
        await using var provider = BuildOrderingGraphQLServiceProvider();
        var resolver = provider.GetRequiredService<OrderingGraphQLResolver>();

        // Seed 2 orders via GraphQL mutation
        var firstMutation = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "mutation { createOrder { id orderNumber } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["buyerCompanyName"] = JsonSerializer.SerializeToElement("Munich Automotive GmbH"),
                ["buyerVatNumber"] = JsonSerializer.SerializeToElement("DE111222333"),
                ["paymentTermsDays"] = JsonSerializer.SerializeToElement(30),
                ["shippingDestination"] = JsonSerializer.SerializeToElement("Munich, Germany")
            }));

        var firstOrder = Assert.IsType<PurchaseOrderDto>(firstMutation.Data!["createOrder"]);

        await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "mutation { createOrder { id orderNumber } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["buyerCompanyName"] = JsonSerializer.SerializeToElement("Brussels Logistics NV"),
                ["buyerVatNumber"] = JsonSerializer.SerializeToElement("BE444555666"),
                ["paymentTermsDays"] = JsonSerializer.SerializeToElement(30),
                ["shippingDestination"] = JsonSerializer.SerializeToElement("Antwerp Port, Belgium")
            }));

        // 1. Query all orders + __schema
        var listResponse = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "query { __schema orders { id orderNumber customer { companyName vatNumber } lines { sku quantity } } }"));

        Assert.Null(listResponse.Errors);
        Assert.NotNull(listResponse.Data);
        Assert.True(listResponse.Data.ContainsKey("__schema"));
        var allOrders = Assert.IsAssignableFrom<IReadOnlyList<PurchaseOrderDto>>(listResponse.Data["orders"]);
        Assert.Equal(2, allOrders.Count);

        // 2. Query orders filtered by buyerVatNumber
        var filteredResponse = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "query($buyerVatNumber: String) { orders(buyerVatNumber: $buyerVatNumber) { id orderNumber } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["buyerVatNumber"] = JsonSerializer.SerializeToElement("BE444555666")
            }));

        var filteredOrders = Assert.IsAssignableFrom<IReadOnlyList<PurchaseOrderDto>>(filteredResponse.Data!["orders"]);
        Assert.Single(filteredOrders);
        Assert.Equal("Brussels Logistics NV", filteredOrders[0].Customer.CompanyName);

        // 3. Query specific order by ID (orderById)
        var byIdResponse = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "query($id: ID!) { orderById(id: $id) { id orderNumber customer { companyName } lines { sku } } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["id"] = JsonSerializer.SerializeToElement(firstOrder.Id.ToString())
            }));

        Assert.Null(byIdResponse.Errors);
        var fetchedOrder = Assert.IsType<PurchaseOrderDto>(byIdResponse.Data!["orderById"]);
        Assert.Equal(firstOrder.Id, fetchedOrder.Id);
        Assert.Equal("Munich Automotive GmbH", fetchedOrder.Customer.CompanyName);

        // 4. Query non-existent orderById -> returns null in data["orderById"]
        var notFoundResponse = await resolver.ExecuteAsync(new OrderingGraphQLRequest(
            Query: "query($id: ID!) { orderById(id: $id) { id } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["id"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString())
            }));

        Assert.Null(notFoundResponse.Errors);
        Assert.Null(notFoundResponse.Data!["orderById"]);
    }

    [Fact]
    public async Task OrderingGraphQLResolver_Should_Return_Errors_On_Empty_Or_Unknown_Fields()
    {
        await using var provider = BuildOrderingGraphQLServiceProvider();
        var resolver = provider.GetRequiredService<OrderingGraphQLResolver>();

        var emptyRes = await resolver.ExecuteAsync(new OrderingGraphQLRequest("   "));
        Assert.NotNull(emptyRes.Errors);
        Assert.NotEmpty(emptyRes.Errors);

        var unknownQueryRes = await resolver.ExecuteAsync(new OrderingGraphQLRequest("query { nonExistentField }"));
        Assert.NotNull(unknownQueryRes.Errors);

        var unknownMutRes = await resolver.ExecuteAsync(new OrderingGraphQLRequest("mutation { nonExistentMutation }"));
        Assert.NotNull(unknownMutRes.Errors);
    }

    [Fact]
    public async Task Ordering_Http_GraphQL_Endpoints_Should_Create_And_Query_Order_By_Id()
    {
        using var client = _factory.CreateClient();

        // 1. Create order via Ordering Subgraph HTTP endpoint (/api/ordering/graphql)
        var createReq = new OrderingGraphQLRequest(
            Query: "mutation { createOrder { id orderNumber customer { companyName vatNumber } lines { sku quantity } } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["buyerCompanyName"] = JsonSerializer.SerializeToElement("Milan Fiber Networks SRL"),
                ["buyerVatNumber"] = JsonSerializer.SerializeToElement("IT99887766554"),
                ["paymentTermsDays"] = JsonSerializer.SerializeToElement(30),
                ["shippingDestination"] = JsonSerializer.SerializeToElement("Via Tortona, Milano")
            });

        var httpRes = await client.PostAsJsonAsync("/api/ordering/graphql", createReq);
        Assert.Equal(HttpStatusCode.OK, httpRes.StatusCode);

        var body = await httpRes.Content.ReadFromJsonAsync<OrderingGraphQLResponse>();
        Assert.NotNull(body?.Data);
        Assert.True(body.Data.ContainsKey("createOrder"));

        var orderJson = (JsonElement)body.Data["createOrder"]!;
        var createdId = orderJson.GetProperty("id").GetString()!;

        // 2. Query specific order by ID via Federated Gateway (/graphql -> orderById)
        var federatedByIdReq = new GraphQLRequest(
            Query: "query($id: ID!) { orderById(id: $id) { id orderNumber customer { companyName vatNumber } invoice { invoiceNumber totalTtc } shipment { trackingNumber carrierName } } }",
            Variables: new Dictionary<string, JsonElement>
            {
                ["id"] = JsonSerializer.SerializeToElement(createdId)
            });

        var fedRes = await client.PostAsJsonAsync("/graphql", federatedByIdReq);
        Assert.Equal(HttpStatusCode.OK, fedRes.StatusCode);
        var fedBody = await fedRes.Content.ReadFromJsonAsync<GraphQLResponse>();
        Assert.Null(fedBody?.Errors);
        Assert.NotNull(fedBody?.Data?["orderById"]);
    }
}
